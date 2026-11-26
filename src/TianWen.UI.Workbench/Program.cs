using System.CommandLine;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.Workbench;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Shared;
using static SDL3.SDL;

// tianwen-edit, the processing application: it opens a `tianwen workbench` project and shows one image's guided screen.
// This file is wiring only. What a test should reach (the input, the between-frames steps, the screen) is WorkbenchHost.

var services = new ServiceCollection();
services
#if DEBUG
    .AddFileLogging("Edit", LogLevel.Debug)
#else
    .AddFileLogging("Edit")
#endif
    .AddFitsViewer()
    .AddExternal()
    .AddAstrometry()
    // The enhancers the workbench's enhance steps run on, then the operations (AddWorkbench says why it comes after).
    .AddRcAstroAi()
    .AddWorkbench()
    .AddSingleton<BackgroundTaskTracker>()
    .AddSingleton<ViewerController>();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TianWen.UI.Workbench");
// A windowed app has no console to die on: whatever takes it down, and a task's exception nobody awaited, go to its log (#1811).
UnhandledExceptionLog.HookForTheProcess(logger);
SdlVulkanLog.Logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SdlVulkan.Renderer");
var controller = sp.GetRequiredService<ViewerController>();
var tracker = sp.GetRequiredService<BackgroundTaskTracker>();

var projectArg = new Argument<string?>("project")
{
    Description = "The workbench project folder (or its project.json) to open",
    Arity = ArgumentArity.ZeroOrOne,
};
var imageOption = new Option<string?>("--image") { Description = "The image of the project to show (default: the first)" };
var rootCommand = new RootCommand($"Tianwen Edit, the processing application {BuildInfo.Describe()} at {BuildInfo.InstallFolder}")
{
    projectArg,
    imageOption,
};

var actionCalled = false;
string? projectPath = null;
string? imageName = null;
rootCommand.SetAction(parseResult =>
{
    actionCalled = true;
    projectPath = parseResult.GetValue(projectArg);
    imageName = parseResult.GetValue(imageOption);
    return Task.CompletedTask;
});

var parsed = rootCommand.Parse(args);
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
    {
        logger.LogError("{Error}", error.Message);
    }

    return 1;
}

await parsed.InvokeAsync();
if (!actionCalled)
{
    return 0;
}

if (projectPath is not null && !Directory.Exists(projectPath) && !File.Exists(projectPath))
{
    logger.LogError("Project not found: {Path}", projectPath);
    return 1;
}

// Who the app is to the OS, before its first window.
var appIdentity = DesktopAppIdentity.Workbench;
var startMenuShortcut = AppIdentity.Claim(appIdentity.Id, appIdentity.DisplayName);
if (startMenuShortcut is StartMenuShortcut.Foreign or StartMenuShortcut.Failed)
{
    logger.LogWarning("App identity {Id}: Start menu shortcut {Shortcut}; the OS may show this app as an unknown app", appIdentity.Id, startMenuShortcut);
}

NativeLoaderDiagnostics.Install(logger);
using var sdlWindow = NativeLoaderDiagnostics.InitNative(logger, "SDL3 + Vulkan window",
    () => SdlVulkanWindow.Create("Tianwen Edit", 1536, 1000, appMetadata: appIdentity.SdlMetadataFor(startMenuShortcut == StartMenuShortcut.Packaged)));
sp.GetRequiredService<HostFileDialogs>().Bind(new SdlFileDialogs(sdlWindow));
sdlWindow.GetSizeInPixels(out var pixW, out var pixH);

var bus = new SignalBus();
using var gpu = new GpuStack<VkImageRenderer>(logger, sdlWindow, (uint)pixW, (uint)pixH,
    r => new VkImageRenderer(r, (uint)pixW, (uint)pixH)
    {
        Bus = bus,
        DpiScale = sdlWindow.DisplayScale,
        Tracker = tracker,
        Logger = logger,
        SetClipboardText = text => SetClipboardText(text),
        // The viewer inside the canvas shows one picture and nothing else: the host cuts the toolbar to the display controls.
        Offer = WorkbenchHost<VulkanContext>.ViewerOffer,
        UseCachedImageLayer = !string.Equals(
            Environment.GetEnvironmentVariable("TIANWEN_EDIT_CACHED_LAYER"), "0", StringComparison.Ordinal),
    });
var renderer = gpu.Renderer;
var imageRenderer = gpu.Top;

using var cts = new CancellationTokenSource();
var workbenchState = new WorkbenchState();
var actions = new WorkbenchActions(workbenchState, state, sp.GetRequiredService<WorkbenchRunner>(),
    sp.GetRequiredService<OperationCatalog>(), tracker, sp.GetRequiredService<IFileDialogs>(),
    text => SetClipboardText(text), logger, cts.Token);
var host = new WorkbenchHost<VulkanContext>(renderer, imageRenderer, state, controller, workbenchState, actions, tracker, bus,
    logger, cts.Token);
host.Resize((uint)pixW, (uint)pixH);
if (projectPath is not null)
{
    host.OpenProject(projectPath, imageName);
}

renderer.OnPreRenderPass = _ =>
{
    imageRenderer.PrepareFrame(controller.Source, state);
    imageRenderer.PrepareCachedImageLayer();
};

var canvasBackground = new RGBAColor32(0x1a, 0x1a, 0x1a, 0xff);
imageRenderer.CanvasBackground = canvasBackground;

var loop = new SdlEventLoop(sdlWindow, renderer)
{
    BackgroundColor = canvasBackground,
    OnResize = (rw, rh) =>
    {
        imageRenderer.DpiScale = sdlWindow.DisplayScale;
        host.Resize(rw, rh);
    },
    OnPointerInput = evt =>
    {
        var handled = host.HandlePointer(evt);
        if (evt is InputEvent.MouseMove)
        {
            UpdateCursor();
        }

        return handled;
    },
    OnPinch = host.HandlePinch,
    OnPinchEnd = host.HandlePinchEnd,
    CheckNeedsRedraw = host.WantsFrame,
    OnRender = () =>
    {
        host.Paint();
        host.PaintTooltip();
        UpdateCursor();
    },
    OnPostFrame = host.AfterFrame,
};

bus.Subscribe<RequestExitSignal>(_ => loop.Stop());
bus.Subscribe<ToggleFullscreenSignal>(_ => sdlWindow.ToggleFullscreen());
bus.Subscribe<OpenUrlSignal>(sig =>
{
    try
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sig.Url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to open URL {Url}", sig.Url);
    }
});

var frameDamage = new List<RectF32>();
loop.OnBeforeFrame = () =>
{
    host.BeforeFrame();
    frameDamage.Clear();
    if (imageRenderer.TryTakeFrameDamage(frameDamage))
    {
        foreach (var r in frameDamage)
        {
            renderer.AddFrameDamage(r.X, r.Y, r.Width, r.Height);
        }
    }
    else
    {
        renderer.MarkFullFrameDamage();
    }
};
loop.OnKeyDown = host.HandleKeyDown;
loop.OnKeyUp = host.HandleKeyUp;

#if SIBLING_DEBUG_INSPECTORS
// Live UI debug inspector (DEBUG only, compiled out of Release): exposes this process to the SdlVulkan.Renderer.Inspector sidecar.
using var debugInspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "Edit",
    WindowTitle = () => "Tianwen Edit",
    GetRegions = () => host.Screen.PaintedRegions(),
    GetLayout = () => host.Screen.GetCapturedLayout(),
    AppState = w =>
    {
        var view = workbenchState.View;
        w.Set("project", view?.Folder ?? "");
        w.Set("image", view?.ImageName ?? "");
        w.Set("selectedStep", workbenchState.SelectedStep);
        w.Set("reading", workbenchState.IsReading);
        w.Set("file", controller.Source is AstroImageDocument d ? System.IO.Path.GetFileName(d.FilePath) : "");
    },
});
#endif

loop.Run(cts.Token);

// The window and the Vulkan context can only be destroyed on THIS thread, so the drain neither awaits nor blocks (ShutdownDrain says why).
cts.Cancel();
ShutdownDrain.PumpUntilComplete(loop, controller.ShutdownAsync(), sp.GetRequiredService<TianWen.Lib.Devices.ITimeProvider>().System, logger);
return 0;

void UpdateCursor()
{
    var (mx, my) = state.MouseScreenPosition;
    var cursor = host.Router.CursorAt(mx, my) ?? CursorKind.Default;
    sdlWindow.SetSystemCursor(cursor.ToSystemCursor);
}
