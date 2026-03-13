using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TianWen.UI.OpenGL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.Lib.Extensions;

// Explicitly register GLFW platforms to avoid reflection-based discovery (AOT-incompatible).
GlfwWindowing.RegisterPlatform();
GlfwInput.RegisterPlatform();

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

var opts = WindowOptions.Default;
opts.Size = new Vector2D<int>(1536, 1080);
opts.Title = document is not null
    ? $"TianWen Image Viewer - {Path.GetFileName(initialFilePath)}"
    : "TianWen Image Viewer";
opts.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));

var window = Window.Create(opts);

GL? gl = null;
GlImageRenderer? renderer = null;
var cts = new CancellationTokenSource();
Task? reprocessTask = null;
Task? backgroundTask = null; // For non-pipeline background work (plate solve, file dialog)
CancellationTokenSource? starDetectionCts = null;
Task? starDetectionTask = null;

window.FileDrop += (paths) =>
{
    // Take the first supported image file from the dropped paths
    var imageFile = paths.FirstOrDefault(p => AstroImageDocument.IsSupportedExtension(Path.GetExtension(p)));

    if (imageFile is null && paths.Length > 0 && Directory.Exists(paths[0]))
    {
        // Dropped a folder — scan it
        ViewerActions.ScanFolder(state, paths[0]);
        if (state.ImageFileNames.Count > 0)
        {
            ViewerActions.SelectFile(state, 0);
        }
        state.NeedsRedraw = true;
        return;
    }

    if (imageFile is null)
    {
        return;
    }

    var dir = Path.GetDirectoryName(imageFile);
    if (dir is not null)
    {
        ViewerActions.ScanFolder(state, dir, Path.GetFileName(imageFile));
    }
    state.RequestedFilePath = imageFile;
    state.NeedsRedraw = true;
};

window.Load += () =>
{
    // Enable dark title bar on Windows 11+
    if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32)
    {
        TianWen.UI.OpenGL.WindowHelper.EnableDarkTitleBar(win32.Hwnd);
    }

    gl = window.CreateOpenGL();
    gl.Enable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    var fbSize = window.FramebufferSize;
    var dpiScale = (float)fbSize.X / window.Size.X;

    renderer = new GlImageRenderer(gl, (uint)fbSize.X, (uint)fbSize.Y)
    {
        DpiScale = dpiScale,
        CelestialObjectDB = celestialObjectDB
    };
    // Kick off DB init eagerly so it's ready when user toggles overlays
    _ = celestialObjectDB.WithCancellation(cts.Token);

    var input = window.CreateInput();

    foreach (var kb in input.Keyboards)
    {
        kb.KeyDown += (keyboard, key, _) =>
        {
            state.NeedsRedraw = true;
            var ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);

            if (ctrl)
            {
                switch (key)
                {
                    case Key.Equal or Key.KeypadAdd:
                        ViewerActions.ZoomIn(state);
                        return;
                    case Key.Minus or Key.KeypadSubtract:
                        ViewerActions.ZoomOut(state);
                        return;
                    case Key.Number0 or Key.Keypad0:
                        ViewerActions.ZoomToFit(state);
                        return;
                    case Key.Number1 or Key.Keypad1:
                        ViewerActions.ZoomToActual(state);
                        return;
                    case >= Key.Number2 and <= Key.Number9:
                        ViewerActions.ZoomTo(state, 1f / (key - Key.Number0));
                        return;
                    case >= Key.Keypad2 and <= Key.Keypad9:
                        ViewerActions.ZoomTo(state, 1f / (key - Key.Keypad0));
                        return;
                }
            }

            switch (key)
            {
                case Key.Escape:
                    window.Close();
                    break;
                case Key.F11:
                    window.WindowState = window.WindowState == WindowState.Fullscreen
                        ? WindowState.Normal
                        : WindowState.Fullscreen;
                    break;
                case Key.T:
                    ViewerActions.ToggleStretch(state);
                    break;
                case Key.S:
                    state.ShowStarOverlay = !state.ShowStarOverlay;
                    break;
                case Key.C:
                    if (document is not null)
                    {
                        ViewerActions.CycleChannelView(state, document.UnstretchedImage.ChannelCount);
                    }
                    break;
                case Key.D:
                    ViewerActions.CycleDebayerAlgorithm(state);
                    break;
                case Key.I:
                    state.ShowInfoPanel = !state.ShowInfoPanel;
                    break;
                case Key.L:
                    state.ShowFileList = !state.ShowFileList;
                    break;
                case Key.Equal or Key.KeypadAdd:
                    ViewerActions.CycleStretchPreset(state);
                    break;
                case Key.Minus or Key.KeypadSubtract:
                    ViewerActions.CycleStretchPreset(state, reverse: true);
                    break;
                case Key.B:
                    ViewerActions.CycleCurvesBoost(state);
                    break;
                case Key.G:
                    state.ShowGrid = !state.ShowGrid;
                    break;
                case Key.O:
                    state.ShowOverlays = !state.ShowOverlays;
                    state.NeedsRedraw = true;
                    break;
                case Key.H:
                    ViewerActions.CycleHdr(state);
                    break;
                case Key.V:
                    var shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
                    if (shift)
                    {
                        state.HistogramLogScale = !state.HistogramLogScale;
                    }
                    else
                    {
                        state.ShowHistogram = !state.ShowHistogram;
                    }
                    break;
                case Key.P:
                    if (document is not null && !state.IsPlateSolving && !document.IsPlateSolved)
                    {
                        var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
                        backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
                    }
                    break;
                case Key.F:
                    ViewerActions.ZoomToFit(state);
                    break;
                case Key.R:
                    ViewerActions.ZoomToActual(state);
                    break;
                case Key.Up:
                    if (state.SelectedFileIndex > 0)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                    }
                    break;
                case Key.Down:
                    if (state.SelectedFileIndex < state.ImageFileNames.Count - 1)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
                    }
                    break;
            }
        };
    }

    foreach (var mouse in input.Mice)
    {
        mouse.MouseMove += (m, pos) =>
        {
            if (renderer is null)
            {
                return;
            }

            state.NeedsRedraw = true;
            // Silk.NET mouse coords are in logical pixels; scale to framebuffer pixels
            var px = pos.X * renderer.DpiScale;
            var py = pos.Y * renderer.DpiScale;
            state.MouseScreenPosition = (px, py);

            // Handle panning (middle mouse drag)
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
                var (areaW, areaH) = renderer.GetImageAreaSize(state);
                var fileListW = state.ShowFileList ? renderer.ScaledFileListWidth : 0;
                var toolbarH = renderer.ScaledToolbarHeight;

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
        };

        mouse.MouseDown += (m, button) =>
        {
            if (renderer is null)
            {
                return;
            }

            state.NeedsRedraw = true;
            var pos = state.MouseScreenPosition;

            if (button == MouseButton.Left)
            {
                // Toolbar hit test
                var toolbarAction = renderer.HitTestToolbar(pos.X, pos.Y, document, state);
                if (toolbarAction.HasValue)
                {
                    HandleToolbarAction(toolbarAction.Value);
                    return;
                }

                // Histogram LOG button hit test
                if (renderer.HitTestHistogramLog(pos.X, pos.Y, state))
                {
                    state.HistogramLogScale = !state.HistogramLogScale;
                    return;
                }

                // File list hit test
                var fileIndex = renderer.HitTestFileList(pos.X, pos.Y, state);
                if (fileIndex >= 0)
                {
                    ViewerActions.SelectFile(state, fileIndex);
                    return;
                }
            }

            if (button == MouseButton.Right)
            {
                // Right-click on toolbar cycles backward
                var toolbarAction = renderer.HitTestToolbar(pos.X, pos.Y, document, state);
                if (toolbarAction.HasValue)
                {
                    HandleToolbarAction(toolbarAction.Value, reverse: true);
                    return;
                }
            }

            // Left or middle mouse button starts panning
            if (button is MouseButton.Left or MouseButton.Middle)
            {
                state.IsPanning = true;
                state.PanStart = pos;
            }
        };

        mouse.MouseUp += (m, button) =>
        {
            state.NeedsRedraw = true;
            if (button is MouseButton.Left or MouseButton.Middle)
            {
                state.IsPanning = false;
            }
        };

        mouse.Scroll += (m, scroll) =>
        {
            if (renderer is null)
            {
                return;
            }
            state.NeedsRedraw = true;
            var pos = state.MouseScreenPosition;

            // Scroll file list when hovering over it
            if (state.ShowFileList && pos.X >= 0 && pos.X < renderer.ScaledFileListWidth && pos.Y > renderer.ScaledToolbarHeight)
            {
                ViewerActions.ScrollFileList(state, -(int)scroll.Y * 3);
                return;
            }

            // Zoom: Ctrl+scroll anywhere, or bare scroll inside the image viewport
            var kb = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
            var ctrlHeld = kb is not null && (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight));
            var fileListW = state.ShowFileList ? renderer.ScaledFileListWidth : 0;
            var toolbarH = renderer.ScaledToolbarHeight;
            var (areaW, areaH) = renderer.GetImageAreaSize(state);
            var inImageViewport = pos.X >= fileListW && pos.X < fileListW + areaW
                               && pos.Y >= toolbarH && pos.Y < toolbarH + areaH;

            if (ctrlHeld || inImageViewport)
            {
                var zoomFactor = scroll.Y > 0 ? 1.15f : 1f / 1.15f;
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
                return;
            }
        };
    }

    // Initial texture upload
    state.NeedsTextureUpdate = true;
};

window.Resize += (size) =>
{
    if (renderer is not null)
    {
        var fb = window.FramebufferSize;
        renderer.DpiScale = (float)fb.X / size.X;
        renderer.Resize((uint)fb.X, (uint)fb.Y);
    }
    state.NeedsRedraw = true;
};

window.Render += (_) =>
{
    if (renderer is null)
    {
        return;
    }

    // Handle file switch request (load on background thread)
    if (state.RequestedFilePath is { } requestedPath && (reprocessTask is null || reprocessTask.IsCompleted))
    {
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
        window.Title = $"TianWen Image Viewer - {Path.GetFileName(requestedPath)}";
    }

    // Handle reprocess flag (just triggers texture re-upload, stretch is done in shader)
    if (state.NeedsReprocess)
    {
        ViewerActions.Reprocess(state);
    }

    // Upload texture when needed (pixel extraction on background, GL upload on render thread)
    if (document is not null && state.NeedsTextureUpdate)
    {
        state.NeedsTextureUpdate = false;
        state.StatusMessage = "Preparing display...";
        var doc = document;
        var channelView = state.ChannelView;

        var image = doc.UnstretchedImage;
        var pixelWidth = image.Width;
        var pixelHeight = image.Height;
        if (channelView is ChannelView.Composite && image.ChannelCount >= 3)
        {
            renderer.ChannelTextureCount = 3; // RGB

            for (var i = 0; i < 3; i++)
            {
                renderer.UploadChannelTexture(image.GetChannelSpan(i), i, pixelWidth, pixelHeight);
            }
        }
        else
        {
            renderer.ChannelTextureCount = 1;

            var channelIndex = channelView switch {
                ChannelView.Composite or ChannelView.Channel0 or ChannelView.Red => 0,
                ChannelView.Channel1 or ChannelView.Green => Math.Min(1, image.ChannelCount - 1),
                ChannelView.Channel2 or ChannelView.Blue => Math.Min(2, image.ChannelCount - 1),
                var cv => throw new InvalidOperationException($"Invalid channel view {cv}")
            };

            renderer.UploadChannelTexture(image.GetChannelSpan(channelIndex), 0, pixelWidth, pixelHeight);
        }

        renderer.UploadHistogramData(doc);
        state.StatusMessage = null;
    }

    renderer.Render(document, state);
};

window.Closing += () =>
{
    cts.Cancel();
    renderer?.Dispose();
    renderer = null;
};

window.Initialize();

while (!window.IsClosing)
{
    window.DoEvents();
    window.DoUpdate();

    if (state.NeedsRedraw || state.NeedsTextureUpdate
        || state.RequestedFilePath is not null
        || (reprocessTask is not null && !reprocessTask.IsCompleted))
    {
        window.DoRender();
        state.NeedsRedraw = false;
    }
    else
    {
        Thread.Sleep(16); // ~60fps idle polling
    }
}

window.DoEvents();
window.Reset();

if (reprocessTask is not null)
{
    try { await reprocessTask; } catch (OperationCanceledException) { }
}
if (backgroundTask is not null)
{
    try { await backgroundTask; } catch (OperationCanceledException) { }
}

return 0;

void HandleToolbarAction(ToolbarAction action, bool reverse = false)
{
    switch (action)
    {
        case ToolbarAction.Open:
            // Run dialog on a background thread to avoid blocking the render loop
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
