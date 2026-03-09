using TianWen.UI.OpenGL;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.Lib.Extensions;

// Explicitly register GLFW platforms to avoid reflection-based discovery (AOT-incompatible).
GlfwWindowing.RegisterPlatform();
GlfwInput.RegisterPlatform();

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
        Console.Error.WriteLine($"Path not found: {inputPath}");
        return 1;
    }
}

// DI setup
var services = new ServiceCollection();
services
    .AddFitsViewer()
    .AddExternal()
    .AddAstrometry();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();

// Scan folder for FITS files
if (folderPath is not null)
{
    ViewerActions.ScanFolder(state, folderPath, initialFilePath is not null ? Path.GetFileName(initialFilePath) : null);
}

// If no specific file was given, try to open the first FITS file in the folder
if (initialFilePath is null && state.FitsFileNames.Count > 0 && folderPath is not null)
{
    initialFilePath = Path.Combine(folderPath, state.FitsFileNames[0]);
    state.SelectedFileIndex = 0;
}

FitsDocument? document = null;
if (initialFilePath is not null)
{
    document = FitsDocument.Open(initialFilePath);
    if (document is null)
    {
        Console.Error.WriteLine($"Warning: Failed to open FITS file: {initialFilePath}");
    }
}

var opts = WindowOptions.Default;
opts.Size = new Vector2D<int>(1280, 900);
opts.Title = document is not null
    ? $"TianWen FITS Viewer - {Path.GetFileName(initialFilePath)}"
    : "TianWen FITS Viewer";
opts.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));

var window = Window.Create(opts);

GL? gl = null;
GlFitsRenderer? renderer = null;
var cts = new CancellationTokenSource();
Task? reprocessTask = null;
Task? backgroundTask = null; // For non-pipeline background work (plate solve, file dialog)
float[][]? pendingChannels = null;
int pendingPixelWidth = 0;
int pendingPixelHeight = 0;

window.FileDrop += (paths) =>
{
    // Take the first FITS file from the dropped paths
    var fitsFile = paths.FirstOrDefault(p =>
        Path.GetExtension(p).Equals(".fit", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(p).Equals(".fits", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(p).Equals(".fts", StringComparison.OrdinalIgnoreCase));

    if (fitsFile is null && paths.Length > 0 && Directory.Exists(paths[0]))
    {
        // Dropped a folder — scan it
        ViewerActions.ScanFolder(state, paths[0]);
        if (state.FitsFileNames.Count > 0)
        {
            ViewerActions.SelectFile(state, 0);
        }
        state.NeedsRedraw = true;
        return;
    }

    if (fitsFile is null)
    {
        return;
    }

    var dir = Path.GetDirectoryName(fitsFile);
    if (dir is not null)
    {
        ViewerActions.ScanFolder(state, dir, Path.GetFileName(fitsFile));
    }
    state.RequestedFilePath = fitsFile;
    state.NeedsRedraw = true;
};

window.Load += () =>
{
    gl = window.CreateOpenGL();
    gl.Enable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    var fbSize = window.FramebufferSize;
    var dpiScale = (float)fbSize.X / window.Size.X;

    renderer = new GlFitsRenderer(gl, (uint)fbSize.X, (uint)fbSize.Y);
    renderer.DpiScale = dpiScale;

    var input = window.CreateInput();

    foreach (var kb in input.Keyboards)
    {
        kb.KeyDown += (_, key, _) =>
        {
            state.NeedsRedraw = true;
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
                case Key.S:
                    ViewerActions.ToggleStretch(state);
                    break;
                case Key.C:
                    if (document is not null)
                    {
                        ViewerActions.CycleChannelView(state, document.DisplayImage.ChannelCount);
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
                case Key.H:
                    ViewerActions.CycleHdr(state);
                    break;
                case Key.P:
                    if (document is not null && !state.IsPlateSolving)
                    {
                        var factory = sp.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>();
                        backgroundTask = ViewerActions.PlateSolveAsync(document, state, factory, cts.Token);
                    }
                    break;
                case Key.F:
                    ViewerActions.ZoomToFit(state);
                    break;
                case Key.Number1:
                    ViewerActions.ZoomToActual(state);
                    break;
                case Key.Up:
                    if (state.SelectedFileIndex > 0)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                    }
                    break;
                case Key.Down:
                    if (state.SelectedFileIndex < state.FitsFileNames.Count - 1)
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

            if (document?.DisplayImage is null)
            {
                return;
            }

            // Convert screen position to image coordinates
            var (areaW, areaH) = renderer.GetImageAreaSize(state);
            var fileListW = state.ShowFileList ? renderer.ScaledFileListWidth : 0;
            var toolbarH = renderer.ScaledToolbarHeight;

            var scale = state.Zoom;
            var drawW = document.DisplayImage.Width * scale;
            var drawH = document.DisplayImage.Height * scale;
            var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
            var offsetY = toolbarH + (areaH - drawH) / 2f + state.PanOffset.Y;

            var imgX = (int)((px - offsetX) / scale);
            var imgY = (int)((py - offsetY) / scale);

            if (imgX >= 0 && imgX < document.DisplayImage.Width && imgY >= 0 && imgY < document.DisplayImage.Height)
            {
                ViewerActions.UpdateCursorInfo(document, state, imgX, imgY);
            }
            else
            {
                state.CursorImagePosition = null;
                state.CursorPixelInfo = null;
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

            // Ctrl+scroll zooms the image toward the cursor
            var kb = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
            if (kb is not null && (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight)))
            {
                var zoomFactor = scroll.Y > 0 ? 1.15f : 1f / 1.15f;
                var oldZoom = state.Zoom;
                var newZoom = MathF.Max(0.01f, oldZoom * zoomFactor);

                // Adjust pan so the point under the cursor stays fixed
                var (areaW, areaH) = renderer.GetImageAreaSize(state);
                var fileListW = state.ShowFileList ? renderer.ScaledFileListWidth : 0;
                var toolbarH = renderer.ScaledToolbarHeight;

                // Cursor position relative to the image area center
                var cx = pos.X - fileListW - areaW / 2f - state.PanOffset.X;
                var cy = pos.Y - toolbarH - areaH / 2f - state.PanOffset.Y;

                // Scale the offset so the world point under cursor stays put
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

    // Initial processing
    state.NeedsReprocess = true;
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
        reprocessTask = Task.Run(() =>
        {
            var newDoc = FitsDocument.Open(requestedPath);
            if (newDoc is not null)
            {
                document = newDoc;
                state.NeedsReprocess = true;
                state.NeedsTextureUpdate = true;
                state.CursorImagePosition = null;
                state.CursorPixelInfo = null;
                state.StatusMessage = null;
            }
            else
            {
                state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
            }
        }, cts.Token);
        // Update title immediately
        window.Title = $"TianWen FITS Viewer - {Path.GetFileName(requestedPath)}";
    }

    // Handle async reprocessing (debayer + stretch on background thread)
    if (document is not null && state.NeedsReprocess && (reprocessTask is null || reprocessTask.IsCompleted))
    {
        reprocessTask = Task.Run(() => ViewerActions.ReprocessAsync(document, state, cts.Token), cts.Token);
    }

    // Upload texture when needed (pixel extraction on background, GL upload on render thread)
    if (document is not null && state.NeedsTextureUpdate && !state.NeedsReprocess && (reprocessTask is null || reprocessTask.IsCompleted))
    {
        state.NeedsTextureUpdate = false;
        state.StatusMessage = "Preparing display...";
        var doc = document;
        var channelView = state.ChannelView;
        reprocessTask = Task.Run(() => doc.GetChannelArrays(channelView), cts.Token)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    pendingChannels = t.Result;
                    pendingPixelWidth = doc.DisplayImage.Width;
                    pendingPixelHeight = doc.DisplayImage.Height;
                }
                state.StatusMessage = null;
            }, TaskScheduler.Default);
    }

    // Upload pending channel textures on the render thread (GL calls must happen here)
    if (pendingChannels is not null)
    {
        renderer.UploadChannelTextures(pendingChannels, pendingPixelWidth, pendingPixelHeight);
        pendingChannels = null;
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

    if (state.NeedsRedraw || state.NeedsReprocess || state.NeedsTextureUpdate
        || pendingChannels is not null
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
            backgroundTask = Task.Run(() =>
            {
                var picked = FileDialogHelper.Pick();
                state.StatusMessage = null;
                if (picked is null)
                {
                    return;
                }

                if (Directory.Exists(picked))
                {
                    ViewerActions.ScanFolder(state, picked);
                    if (state.FitsFileNames.Count > 0)
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
                ViewerActions.CycleChannelView(state, document.DisplayImage.ChannelCount);
            }
            break;
        case ToolbarAction.Debayer:
            ViewerActions.CycleDebayerAlgorithm(state);
            break;
        case ToolbarAction.CurvesBoost:
            ViewerActions.CycleCurvesBoost(state, reverse);
            break;
        case ToolbarAction.Hdr:
            ViewerActions.CycleHdr(state, reverse);
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
