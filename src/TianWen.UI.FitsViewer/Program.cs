using System.Runtime.InteropServices;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Shared;
using TianWen.Lib.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static SDL3.SDL;

// DI setup — before args processing so logger is available for early errors
var services = new ServiceCollection();
services
    .AddFileLogging("FitsViewer")
    .AddFitsViewer()
    .AddExternal()
    .AddAstrometry();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<IExternal>().AppLogger;

string? initialFilePath = null;
string? folderPath = null;

if (args.Length >= 1)
{
    var inputPath = args[0];

    if (Directory.Exists(inputPath))
    {
        folderPath = Path.GetFullPath(inputPath);
    }
    else if (File.Exists(inputPath))
    {
        initialFilePath = Path.GetFullPath(inputPath);
        folderPath = Path.GetDirectoryName(initialFilePath);
    }
    else
    {
        logger.LogError("Path not found: {InputPath}", inputPath);
        return 1;
    }
}

// Lazy-initialized catalog DB — starts init on first access, safe to pass around immediately
var celestialObjectDB = new DotNext.Threading.AsyncLazy<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(async (ct) =>
{
    var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
    await db.InitDBAsync(ct);
    return db;
});

// Scan folder for supported image files
if (folderPath is not null)
{
    ViewerActions.ScanFolder(state, folderPath, initialFilePath is not null ? Path.GetFileName(initialFilePath) : null);
}

// If no specific file was given, try to open the first image in the folder
if (initialFilePath is null && state.ImageFileNames.Count > 0 && folderPath is not null)
{
    initialFilePath = Path.Combine(folderPath, state.ImageFileNames[0]);
    state.SelectedFileIndex = 0;
}

AstroImageDocument? document = null;
var documentCache = new DocumentCache();
if (initialFilePath is not null)
{
    // Defer loading so the window appears immediately with a status message
    state.RequestedFilePath = initialFilePath;
}

// --- SDL3 + Vulkan init ---
using var sdlWindow = SdlVulkanWindow.Create("TianWen Image Viewer", 1536, 1080);
sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var ctx = VulkanContext.Create(sdlWindow.Instance, sdlWindow.Surface, (uint)pixW, (uint)pixH);
var renderer = new VkRenderer(ctx, (uint)pixW, (uint)pixH);

// Compute DPI scale from window size vs pixel size
var dpiScale = sdlWindow.DisplayScale;

var imageRenderer = new VkImageRenderer(renderer, (uint)pixW, (uint)pixH)
{
    DpiScale = dpiScale,
    CelestialObjectDB = celestialObjectDB
};

// Kick off DB init eagerly so it's ready when user toggles overlays
var cts = new CancellationTokenSource();
_ = celestialObjectDB.WithCancellation(cts.Token);

Task? reprocessTask = null;
Task? backgroundTask = null;
CancellationTokenSource? starDetectionCts = null;
Task? starDetectionTask = null;

var needsRedraw = true;
var running = true;

// Track mouse position
var mouseX = 0f;
var mouseY = 0f;

while (running)
{
    Event evt;
    var hadEvent = needsRedraw
        ? PollEvent(out evt)
        : WaitEventTimeout(out evt, 16);

    if (hadEvent)
    {
        do
        {
            switch ((EventType)evt.Type)
            {
                case EventType.Quit:
                    running = false;
                    break;

                case EventType.WindowResized:
                case EventType.WindowPixelSizeChanged:
                    sdlWindow.GetSizeInPixels(out var rw, out var rh);
                    if (rw > 0 && rh > 0)
                    {
                        dpiScale = sdlWindow.DisplayScale;
                        renderer.Resize((uint)rw, (uint)rh);
                        imageRenderer.DpiScale = dpiScale;
                        imageRenderer.Resize((uint)rw, (uint)rh);
                    }
                    needsRedraw = true;
                    break;

                case EventType.WindowExposed:
                    needsRedraw = true;
                    break;

                case EventType.KeyDown:
                    needsRedraw = true;
                    HandleKeyDown(evt.Key.Scancode, evt.Key.Mod);
                    break;

                case EventType.MouseMotion:
                    needsRedraw = true;
                    HandleMouseMove(evt.Motion.X, evt.Motion.Y);
                    break;

                case EventType.MouseButtonDown:
                    needsRedraw = true;
                    HandleMouseDown(evt.Button.Button, evt.Button.X, evt.Button.Y);
                    break;

                case EventType.MouseButtonUp:
                    needsRedraw = true;
                    HandleMouseUp(evt.Button.Button);
                    break;

                case EventType.MouseWheel:
                    needsRedraw = true;
                    HandleMouseWheel(evt.Wheel.Y);
                    break;

                case EventType.DropFile:
                    needsRedraw = true;
                    HandleFileDrop(Marshal.PtrToStringUTF8(evt.Drop.Data));
                    break;
            }
        } while (PollEvent(out evt));
    }

    // Check if background tasks completed
    if (state.NeedsRedraw || state.NeedsTextureUpdate || state.RequestedFilePath is not null
        || (reprocessTask is not null && !reprocessTask.IsCompleted))
    {
        needsRedraw = true;
    }

    if (!needsRedraw)
    {
        continue;
    }
    needsRedraw = false;

    // Handle file switch request (load on background thread)
    HandleFileRequest();

    // Handle reprocess flag
    if (state.NeedsReprocess)
    {
        ViewerActions.Reprocess(state);
    }

    // Upload textures when needed
    HandleTextureUpload();

    // --- Render frame ---
    var bgColor = new RGBAColor32(0x1a, 0x1a, 0x1a, 0xff);
    if (!renderer.BeginFrame(bgColor))
    {
        sdlWindow.GetSizeInPixels(out var sw, out var sh);
        if (sw > 0 && sh > 0)
        {
            renderer.Resize((uint)sw, (uint)sh);
            imageRenderer.Resize((uint)sw, (uint)sh);
        }
        needsRedraw = true;
        continue;
    }

    imageRenderer.Render(document, state);

    renderer.EndFrame();

    if (renderer.FontAtlasDirty)
    {
        needsRedraw = true;
    }
    state.NeedsRedraw = false;
}

// Cleanup
cts.Cancel();
imageRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

if (reprocessTask is not null)
{
    try { await reprocessTask; } catch (OperationCanceledException) { }
}
if (backgroundTask is not null)
{
    try { await backgroundTask; } catch (OperationCanceledException) { }
}

return 0;

// --- Event handlers ---

void HandleKeyDown(Scancode scancode, Keymod keymod)
{
    var ctrl = (keymod & Keymod.Ctrl) != 0;
    var shift = (keymod & Keymod.Shift) != 0;

    if (ctrl)
    {
        switch (scancode)
        {
            case Scancode.Equals:
                ViewerActions.ZoomIn(state);
                return;
            case Scancode.Minus:
                ViewerActions.ZoomOut(state);
                return;
            case Scancode.Alpha0:
                ViewerActions.ZoomToFit(state);
                return;
            case Scancode.Alpha1:
                ViewerActions.ZoomToActual(state);
                return;
            case >= Scancode.Alpha2 and <= Scancode.Alpha9:
                ViewerActions.ZoomTo(state, 1f / (scancode - Scancode.Alpha0));
                return;
        }
    }

    switch (scancode)
    {
        case Scancode.Escape:
            running = false;
            break;
        case Scancode.F11:
            sdlWindow.ToggleFullscreen();
            break;
        case Scancode.T:
            ViewerActions.ToggleStretch(state);
            break;
        case Scancode.S:
            state.ShowStarOverlay = !state.ShowStarOverlay;
            break;
        case Scancode.C:
            if (document is not null)
            {
                ViewerActions.CycleChannelView(state, document.UnstretchedImage.ChannelCount);
            }
            break;
        case Scancode.D:
            ViewerActions.CycleDebayerAlgorithm(state);
            break;
        case Scancode.I:
            state.ShowInfoPanel = !state.ShowInfoPanel;
            break;
        case Scancode.L:
            state.ShowFileList = !state.ShowFileList;
            break;
        case Scancode.Equals:
            ViewerActions.CycleStretchPreset(state);
            break;
        case Scancode.Minus:
            ViewerActions.CycleStretchPreset(state, reverse: true);
            break;
        case Scancode.B:
            ViewerActions.CycleCurvesBoost(state);
            break;
        case Scancode.G:
            state.ShowGrid = !state.ShowGrid;
            break;
        case Scancode.O:
            state.ShowOverlays = !state.ShowOverlays;
            state.NeedsRedraw = true;
            break;
        case Scancode.H:
            ViewerActions.CycleHdr(state);
            break;
        case Scancode.V:
            if (shift)
            {
                state.HistogramLogScale = !state.HistogramLogScale;
            }
            else
            {
                state.ShowHistogram = !state.ShowHistogram;
            }
            break;
        case Scancode.P:
            if (document is not null && !state.IsPlateSolving && !document.IsPlateSolved)
            {
                var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
                backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
            }
            break;
        case Scancode.F:
            ViewerActions.ZoomToFit(state);
            break;
        case Scancode.R:
            ViewerActions.ZoomToActual(state);
            break;
        case Scancode.Up:
            if (state.SelectedFileIndex > 0)
            {
                ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
            }
            break;
        case Scancode.Down:
            if (state.SelectedFileIndex < state.ImageFileNames.Count - 1)
            {
                ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
            }
            break;
    }
}

void HandleMouseMove(float px, float py)
{
    mouseX = px;
    mouseY = py;
    state.MouseScreenPosition = (px, py);

    // Handle panning
    if (state.IsPanning)
    {
        var dx = px - state.PanStart.X;
        var dy = py - state.PanStart.Y;
        state.PanOffset = (state.PanOffset.X + dx, state.PanOffset.Y + dy);
        state.PanStart = (px, py);
    }

    if (document?.UnstretchedImage is { } image)
    {
        // Convert screen position to image coordinates
        var (areaW, areaH) = imageRenderer.GetImageAreaSize(state);
        var fileListW = state.ShowFileList ? imageRenderer.ScaledFileListWidth : 0;
        var toolbarH = imageRenderer.ScaledToolbarHeight;

        var scale = state.Zoom;
        var drawW = image.Width * scale;
        var drawH = image.Height * scale;
        var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
        var offsetY = toolbarH + (areaH - drawH) / 2f + state.PanOffset.Y;

        var imgX = (int)((px - offsetX) / scale);
        var imgY = (int)((py - offsetY) / scale);

        if (imgX >= 0 && imgX < image.Width && imgY >= 0 && imgY < image.Height)
        {
            ViewerActions.UpdateCursorInfo(document, state, imgX, imgY);
        }
        else
        {
            state.CursorImagePosition = null;
            state.CursorPixelInfo = null;
        }
    }
}

void HandleMouseDown(byte button, float px, float py)
{
    mouseX = px;
    mouseY = py;
    state.MouseScreenPosition = (px, py);

    if (button is 1 or 3)
    {
        // Unified hit test — OnClick handlers fire for self-contained actions (e.g. HistogramLog)
        var hit = imageRenderer.HitTestAndDispatch(px, py);

        if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
        {
            HandleToolbarAction(toolbarAction, reverse: button == 3);
            return;
        }

        if (hit is HitResult.ListItemHit { ListId: "FileList", Index: var fileIndex })
        {
            ViewerActions.SelectFile(state, fileIndex);
            return;
        }

        if (hit is not null)
        {
            return; // OnClick already handled it (e.g. HistogramLog)
        }
    }

    // Left or middle mouse button starts panning
    if (button is 1 or 2) // SDL: 1=left, 2=middle, 3=right
    {
        state.IsPanning = true;
        state.PanStart = (px, py);
    }
}

void HandleMouseUp(byte button)
{
    if (button is 1 or 2)
    {
        state.IsPanning = false;
    }
}

void HandleMouseWheel(float scrollY)
{
    var pos = state.MouseScreenPosition;

    // Scroll file list when hovering over it
    if (state.ShowFileList && pos.X >= 0 && pos.X < imageRenderer.ScaledFileListWidth && pos.Y > imageRenderer.ScaledToolbarHeight)
    {
        ViewerActions.ScrollFileList(state, -(int)scrollY * 3);
        return;
    }

    // Zoom: Ctrl+scroll anywhere, or bare scroll inside the image viewport
    var modState = GetModState();
    var ctrlHeld = (modState & Keymod.Ctrl) != 0;
    var fileListW = state.ShowFileList ? imageRenderer.ScaledFileListWidth : 0;
    var toolbarH = imageRenderer.ScaledToolbarHeight;
    var (areaW, areaH) = imageRenderer.GetImageAreaSize(state);
    var inImageViewport = pos.X >= fileListW && pos.X < fileListW + areaW
                       && pos.Y >= toolbarH && pos.Y < toolbarH + areaH;

    if (ctrlHeld || inImageViewport)
    {
        var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
        var oldZoom = state.Zoom;
        var newZoom = MathF.Max(0.01f, oldZoom * zoomFactor);

        // Adjust pan so the point under the cursor stays fixed
        var cx = pos.X - fileListW - areaW / 2f - state.PanOffset.X;
        var cy = pos.Y - toolbarH - areaH / 2f - state.PanOffset.Y;

        state.PanOffset = (
            state.PanOffset.X - cx * (newZoom / oldZoom - 1f),
            state.PanOffset.Y - cy * (newZoom / oldZoom - 1f)
        );

        state.ZoomToFit = false;
        state.Zoom = newZoom;
    }
}

void HandleFileDrop(string? path)
{
    if (path is null)
    {
        return;
    }

    if (Directory.Exists(path))
    {
        ViewerActions.ScanFolder(state, path);
        if (state.ImageFileNames.Count > 0)
        {
            ViewerActions.SelectFile(state, 0);
        }
        state.NeedsRedraw = true;
        return;
    }

    if (File.Exists(path) && AstroImageDocument.IsSupportedExtension(Path.GetExtension(path)))
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            ViewerActions.ScanFolder(state, dir, Path.GetFileName(path));
        }
        state.RequestedFilePath = path;
        state.NeedsRedraw = true;
    }
}

void HandleFileRequest()
{
    if (state.RequestedFilePath is not { } requestedPath || (reprocessTask is not null && !reprocessTask.IsCompleted))
    {
        return;
    }

    state.RequestedFilePath = null;
    state.StatusMessage = $"Loading {Path.GetFileName(requestedPath)}...";
    // Cancel any in-progress star detection from previous image
    starDetectionCts?.Cancel();
    starDetectionCts?.Dispose();
    starDetectionCts = null;
    var debayerAlgorithm = state.DebayerAlgorithm;
    reprocessTask = Task.Run(async () =>
    {
        var newDoc = await documentCache.GetOrLoadAsync(requestedPath, debayerAlgorithm, cts.Token);
        if (newDoc is not null)
        {
            document = newDoc;
            state.NeedsTextureUpdate = true;
            state.CursorImagePosition = null;
            state.CursorPixelInfo = null;
            state.StatusMessage = null;

            // Disable stretch for pre-stretched images, re-enable for linear images
            state.StretchMode = newDoc.IsPreStretched ? StretchMode.None : StretchMode.Unlinked;
            state.HistogramLogScale = state.StretchMode is StretchMode.None;

            if (newDoc.Wcs is { } wcs)
            {
                logger.LogInformation("WCS: HasCD={HasCDMatrix}, Approx={IsApproximate}, Scale={PixelScale:F2}\"/px, RA={CenterRA:F4}h, Dec={CenterDec:F4}°",
                    wcs.HasCDMatrix, wcs.IsApproximate, wcs.PixelScaleArcsec, wcs.CenterRA, wcs.CenterDec);
            }

            // Kick off star detection in the background
            var sdCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            starDetectionCts = sdCts;
            starDetectionTask = Task.Run(async () =>
            {
                try
                {
                    await newDoc.DetectStarsAsync(sdCts.Token);
                    logger.LogInformation("Detected {StarCount} stars in {Duration:F1}s (HFR={HFR:F2}, FWHM={FWHM:F2})",
                        newDoc.Stars?.Count ?? 0, newDoc.StarDetectionDuration.TotalSeconds, newDoc.AverageHFR, newDoc.AverageFWHM);
                    state.NeedsRedraw = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Star detection failed");
                    newDoc.Stars = StarList.Empty;
                    state.StatusMessage = "Star detection failed";
                    state.NeedsRedraw = true;
                }
            }, sdCts.Token);
        }
        else
        {
            logger.LogWarning("Failed to open image file: {FilePath}", requestedPath);
            state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
        }
    }, cts.Token);

    // Update title immediately
    SetWindowTitle(sdlWindow.Handle, $"TianWen Image Viewer - {Path.GetFileName(requestedPath)}");
}

void HandleTextureUpload()
{
    if (document is null || !state.NeedsTextureUpdate)
    {
        return;
    }

    state.NeedsTextureUpdate = false;
    state.StatusMessage = "Preparing display...";
    var doc = document;
    var channelView = state.ChannelView;

    var image = doc.UnstretchedImage;
    var pixelWidth = image.Width;
    var pixelHeight = image.Height;
    if (channelView is ChannelView.Composite && image.ChannelCount >= 3)
    {
        imageRenderer.ChannelTextureCount = 3;

        for (var i = 0; i < 3; i++)
        {
            imageRenderer.UploadChannelTexture(image.GetChannelSpan(i), i, pixelWidth, pixelHeight);
        }
    }
    else
    {
        imageRenderer.ChannelTextureCount = 1;

        var channelIndex = channelView switch
        {
            ChannelView.Composite or ChannelView.Channel0 or ChannelView.Red => 0,
            ChannelView.Channel1 or ChannelView.Green => Math.Min(1, image.ChannelCount - 1),
            ChannelView.Channel2 or ChannelView.Blue => Math.Min(2, image.ChannelCount - 1),
            var cv => throw new InvalidOperationException($"Invalid channel view {cv}")
        };

        imageRenderer.UploadChannelTexture(image.GetChannelSpan(channelIndex), 0, pixelWidth, pixelHeight);
    }

    imageRenderer.UploadHistogramData(doc);
    state.StatusMessage = null;
}

void HandleToolbarAction(ToolbarAction action, bool reverse = false)
{
    switch (action)
    {
        case ToolbarAction.Open:
            state.StatusMessage = "Opening file dialog...";
            backgroundTask = Task.Run(async () =>
            {
                var filters = AstroImageDocument.FileDialogFilters
                    .ToDictionary(f => f.Name, f => (IReadOnlyList<string>)f.Extensions);
                var picked = await FileDialogHelper.PickAsync(filters, combinedFilterName: "All supported images", title: "Open image").ConfigureAwait(false);
                state.StatusMessage = null;
                if (picked is null)
                {
                    return;
                }

                if (Directory.Exists(picked))
                {
                    ViewerActions.ScanFolder(state, picked);
                    if (state.ImageFileNames.Count > 0)
                    {
                        ViewerActions.SelectFile(state, 0);
                    }
                }
                else if (File.Exists(picked))
                {
                    var dir = Path.GetDirectoryName(picked);
                    if (dir is not null)
                    {
                        ViewerActions.ScanFolder(state, dir, Path.GetFileName(picked));
                    }
                    state.RequestedFilePath = picked;
                }
            }, cts.Token);
            break;
        case ToolbarAction.StretchToggle:
            ViewerActions.ToggleStretch(state);
            break;
        case ToolbarAction.StretchLink:
            ViewerActions.CycleStretchLink(state, reverse);
            break;
        case ToolbarAction.StretchParams:
            ViewerActions.CycleStretchPreset(state, reverse);
            break;
        case ToolbarAction.Channel:
            if (document is not null)
            {
                ViewerActions.CycleChannelView(state, document.UnstretchedImage.ChannelCount);
            }
            break;
        case ToolbarAction.Debayer:
            ViewerActions.CycleDebayerAlgorithm(state, reverse);
            break;
        case ToolbarAction.CurvesBoost:
            ViewerActions.CycleCurvesBoost(state, reverse);
            break;
        case ToolbarAction.Hdr:
            ViewerActions.CycleHdr(state, reverse);
            break;
        case ToolbarAction.Grid:
            state.ShowGrid = !state.ShowGrid;
            state.NeedsRedraw = true;
            break;
        case ToolbarAction.Overlays:
            state.ShowOverlays = !state.ShowOverlays;
            state.NeedsRedraw = true;
            break;
        case ToolbarAction.Stars:
            state.ShowStarOverlay = !state.ShowStarOverlay;
            state.NeedsRedraw = true;
            break;
        case ToolbarAction.ZoomFit:
            ViewerActions.ZoomToFit(state);
            break;
        case ToolbarAction.ZoomActual:
            ViewerActions.ZoomToActual(state);
            break;
        case ToolbarAction.PlateSolve:
            if (document is not null && !state.IsPlateSolving)
            {
                var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
                backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
            }
            break;
    }
}

