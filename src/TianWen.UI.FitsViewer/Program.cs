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

var bus = new SignalBus();
var imageRenderer = new VkImageRenderer(renderer, (uint)pixW, (uint)pixH)
{
    Bus = bus,
    DpiScale = sdlWindow.DisplayScale,
    CelestialObjectDB = celestialObjectDB
};

// Kick off DB init eagerly so it's ready when user toggles overlays
var cts = new CancellationTokenSource();
_ = celestialObjectDB.WithCancellation(cts.Token);

Task? reprocessTask = null;
Task? backgroundTask = null;
CancellationTokenSource? starDetectionCts = null;
Task? starDetectionTask = null;

// --- Main event loop via SdlEventLoop ---

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = new RGBAColor32(0x1a, 0x1a, 0x1a, 0xff),

    OnResize = (rw, rh) =>
    {
        imageRenderer.DpiScale = sdlWindow.DisplayScale;
        imageRenderer.Resize(rw, rh);
    },

    OnMouseDown = (button, x, y, clicks, _) =>
    {
        HandleMouseDown(button, x, y);
        return true;
    },

    OnMouseMove = (x, y) => imageRenderer.HandleInput(new InputEvent.MouseMove(x, y)),

    OnMouseUp = (button) => imageRenderer.HandleInput(new InputEvent.MouseUp(0, 0)),

    OnMouseWheel = (scrollY, mouseX, mouseY) =>
    {
        imageRenderer.HandleInput(new InputEvent.Scroll(scrollY, mouseX, mouseY));
        return true;
    },

    OnDropFile = (path) => { if (path is not null) ViewerActions.HandleFileDrop(state, path); },

    CheckNeedsRedraw = () =>
        state.NeedsRedraw || state.NeedsTextureUpdate || state.RequestedFilePath is not null
        || (reprocessTask is not null && !reprocessTask.IsCompleted),

    OnRender = () =>
    {
        // Handle file switch request (load on background thread)
        HandleFileRequest();

        // Handle reprocess flag
        if (state.NeedsReprocess)
        {
            ViewerActions.Reprocess(state);
        }

        // Upload textures when needed
        if (document is not null && state.NeedsTextureUpdate)
        {
            imageRenderer.UploadDocumentTextures(document, state);
        }

        imageRenderer.Render(document, state);
    },

    OnPostFrame = () =>
    {
        bus.ProcessPending();
        state.NeedsRedraw = false;

        // Release completed task closures so captured documents can be GC'd
        if (reprocessTask is { IsCompleted: true }) reprocessTask = null;
        if (starDetectionTask is { IsCompleted: true }) starDetectionTask = null;
        if (backgroundTask is { IsCompleted: true }) backgroundTask = null;
    }
};

// Signal subscriptions for app-level actions
bus.Subscribe<RequestExitSignal>(_ => loop.Stop());
bus.Subscribe<ToggleFullscreenSignal>(_ => sdlWindow.ToggleFullscreen());
bus.Subscribe<PlateSolveSignal>(_ =>
{
    if (document is not null && !state.IsPlateSolving && !document.IsPlateSolved)
    {
        var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
        backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
    }
});

// OnKeyDown wired separately — imageRenderer.HandleInput handles F11 via signal bus
loop.OnKeyDown = (inputKey, inputModifier) =>
{
    imageRenderer.HandleInput(new InputEvent.KeyDown(inputKey, inputModifier));
    return true;
};

loop.Run(cts.Token);

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

void HandleMouseDown(byte button, float px, float py)
{
    state.MouseScreenPosition = (px, py);

    if (button is 1 or 3)
    {
        // Hit test — base class handles pure state actions (file list, toggles)
        var hit = imageRenderer.HitTestAndDispatch(px, py);

        if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
        {
            // Base handles pure state; we handle DI-dependent + right-click reverse
            if (!ViewerActions.HandleToolbarAction(state, document, toolbarAction, reverse: button == 3))
            {
                HandleToolbarAction(toolbarAction);
            }
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
        ViewerActions.BeginPan(state, px, py);
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

void HandleToolbarAction(ToolbarAction action, bool reverse = false)
{
    if (ViewerActions.HandleToolbarAction(state, document, action, reverse))
    {
        return;
    }

    // DI-dependent actions not handled by ViewerActions
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
        case ToolbarAction.PlateSolve:
            if (document is not null && !state.IsPlateSolving)
            {
                var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
                backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
            }
            break;
    }
}
