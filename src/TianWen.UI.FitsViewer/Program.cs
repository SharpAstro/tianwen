using System.CommandLine;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.FitsViewer;
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
    .AddAstrometry()
    .AddSingleton<ViewerController>();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.FitsViewer");
var controller = sp.GetRequiredService<ViewerController>();

// --- Command-line definition ---
var pathArg = new Argument<string?>("path")
{
    Description = "File or folder to open",
    Arity = ArgumentArity.ZeroOrOne
};

// --register [FITS]: optional value, defaults to "FITS" when specified without a group name
var registerOption = new Option<string?>("--register")
{
    Description = "Register file associations for an extension group (default: FITS)",
    Arity = ArgumentArity.ZeroOrOne
};

var rootCommand = new RootCommand("TianWen FITS Image Viewer")
{
    pathArg,
    registerOption
};

// SetAction captures parsed values; --help/--version bypass this action automatically
var actionCalled = false;
string? registerGroup = null;
string? inputArg = null;

rootCommand.SetAction((parseResult, _) =>
{
    actionCalled = true;

    // ZeroOrOne arity: GetValue returns null both when not specified and when
    // specified without a value. Check raw args to detect the flag's presence.
    if (Array.Exists(args, a => a is "--register"))
    {
        registerGroup = parseResult.GetValue(registerOption) ?? "FITS";
    }

    inputArg = parseResult.GetValue(pathArg);
    return Task.CompletedTask;
});

var parsedResult = rootCommand.Parse(args);
if (parsedResult.Errors.Count > 0)
{
    foreach (var error in parsedResult.Errors)
    {
        logger.LogError("{Error}", error.Message);
    }
    return 1;
}

await parsedResult.InvokeAsync();

// --help/--version bypass SetAction — exit cleanly
if (!actionCalled)
{
    return 0;
}

// --register: register file associations and exit (before SDL init)
if (registerGroup is not null)
{
    return FileAssociationRegistrar.Register(registerGroup, logger);
}

string? initialFilePath = null;
string? folderPath = null;

if (inputArg is not null)
{
    if (Directory.Exists(inputArg))
    {
        folderPath = Path.GetFullPath(inputArg);
    }
    else if (File.Exists(inputArg))
    {
        initialFilePath = Path.GetFullPath(inputArg);
        folderPath = Path.GetDirectoryName(initialFilePath);
    }
    else
    {
        logger.LogError("Path not found: {InputPath}", inputArg);
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

if (initialFilePath is not null)
{
    // Defer loading so the window appears immediately with a status message
    state.RequestedFilePath = initialFilePath;
}

// --- SDL3 + Vulkan init ---
using var sdlWindow = SdlVulkanWindow.Create("Fits viewer", 1536, 1080);
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

// Wire title update from controller
controller.FileLoaded += name => SetWindowTitle(sdlWindow.Handle, Path.GetFileName(name));

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
        || controller.IsLoadPending,

    OnRender = () =>
    {
        controller.HandleFileRequest(cts.Token);

        if (state.NeedsReprocess)
        {
            ViewerActions.Reprocess(state);
        }

        if (controller.Document is not null && state.NeedsTextureUpdate)
        {
            imageRenderer.UploadDocumentTextures(controller.Document, state);
        }

        imageRenderer.Render(controller.Document, state);
    },

    OnPostFrame = () =>
    {
        bus.ProcessPending();
        state.NeedsRedraw = false;
        controller.ReleaseCompletedTasks();
    }
};

// Signal subscriptions for app-level actions
bus.Subscribe<RequestExitSignal>(_ => loop.Stop());
bus.Subscribe<ToggleFullscreenSignal>(_ => sdlWindow.ToggleFullscreen());
bus.Subscribe<PlateSolveSignal>(_ =>
    controller.HandleToolbarAction(ToolbarAction.PlateSolve, reverse: false, cts.Token));

// OnKeyDown wired separately — imageRenderer.HandleInput handles F11 via signal bus
loop.OnKeyDown = (inputKey, inputModifier) =>
{
    imageRenderer.HandleInput(new InputEvent.KeyDown(inputKey, inputModifier));
    return true;
};

loop.Run(cts.Token);

// Cleanup
cts.Cancel();
await controller.ShutdownAsync();
imageRenderer.Dispose();
renderer.Dispose();
ctx.Dispose();

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
            // Base handles pure state; controller handles DI-dependent actions
            if (!ViewerActions.HandleToolbarAction(state, controller.Document, toolbarAction, reverse: button == 3))
            {
                controller.HandleToolbarAction(toolbarAction, reverse: button == 3, cts.Token);
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
