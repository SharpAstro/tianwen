using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-fits</c> without its window: the real <see cref="StandaloneViewerHost{TSurface}"/>, the real
/// <see cref="ViewerController"/> opening real files through the real <see cref="DocumentCache"/>, and the
/// real viewer painting onto a CPU surface. Input goes in the way SDL hands it to <c>Program.cs</c>, and a
/// frame runs the loop's own steps in the loop's own order.
/// </summary>
/// <remarks>
/// <para>
/// What is NOT here is exactly what cannot be: SDL's event pump, the Vulkan upload and draw, the window,
/// the instance gate. A test through this class therefore sees every regression in routing, toolbar
/// policy, document lifecycle and layout, and none in the GPU path. The shader and texture side has its own
/// tests and the live inspector.
/// </para>
/// <para>
/// The file dialog and the plate solver are substitutes: the one blocks on a human and the other on a
/// catalogue, and neither is what a viewer e2e is for. Everything else is the production object.
/// </para>
/// </remarks>
internal sealed class ViewerE2E : IDisposable
{
    internal const int ImageW = 64;
    internal const int ImageH = 48;

    private readonly RgbaImageRenderer _renderer;
    private readonly CancellationTokenSource _appCts = new CancellationTokenSource();
    private int _exitRequests;

    private ViewerE2E(uint width, uint height, float dpiScale)
    {
        _renderer = new RgbaImageRenderer(width, height);
        Bus = new SignalBus();
        Bus.Subscribe<RequestExitSignal>(_ => _exitRequests++);
        State = new ViewerState();
        Tracker = new BackgroundTaskTracker();
        Controller = new ViewerController(State, new DocumentCache(), Substitute.For<IFileDialogHelper>(),
            Substitute.For<IPlateSolverFactory>(), new FakeTimeProviderWrapper(), Tracker,
            NullLogger<ViewerController>.Instance);
        Viewer = new Surface(_renderer, Bus) { DpiScale = dpiScale };
        Host = new StandaloneViewerHost<RgbaImage>(Viewer, State, Controller, Tracker, Bus,
            NullLogger.Instance, _appCts.Token);
        Folder = Directory.CreateTempSubdirectory("tianwen-viewer-e2e-").FullName;
    }

    /// <summary>A viewer the size of an ordinary window at the given DPI scale, with nothing open.</summary>
    internal static ViewerE2E Start(float dpiScale, uint width = 1600, uint height = 1000)
    {
        var e2e = new ViewerE2E(width, height, dpiScale);
        e2e.Frame();
        return e2e;
    }

    internal Surface Viewer { get; }

    internal ViewerState State { get; }

    internal ViewerController Controller { get; }

    internal BackgroundTaskTracker Tracker { get; }

    internal SignalBus Bus { get; }

    internal StandaloneViewerHost<RgbaImage> Host { get; }

    /// <summary>A scratch folder, deleted with the harness, for the files a test opens.</summary>
    internal string Folder { get; }

    /// <summary>How many times the app asked to exit.</summary>
    internal int ExitRequests => _exitRequests;

    /// <summary>
    /// One iteration of the loop, in <c>Program.cs</c>'s order: the between-frames step, the paint, the
    /// after-frame step. Drawn unconditionally, where the loop would ask <c>WantsFrame</c> first: a frame
    /// nobody needed changes nothing, and skipping one a test did need would hide what it checks.
    /// </summary>
    internal void Frame()
    {
        Host.BeforeFrame();
        Viewer.Render(Controller.Source, State);
        Host.AfterFrame();
    }

    /// <summary>
    /// Writes a small three-channel FITS file into <see cref="Folder"/>. Each file's level is offset by
    /// <paramref name="level"/> so two files are distinguishable by their pixels as well as their names.
    /// </summary>
    internal string WriteColourFits(string name, float level = 0f)
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    planes[c][y, x] = level + (1000f * (c + 1)) + (y * ImageW) + x;
                }
            }
        }

        var image = new Image([planes[0], planes[1], planes[2]], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("e2e", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));
        var path = Path.Combine(Folder, name);
        image.WriteToFitsFile(path);
        return path;
    }

    /// <summary>Opens a file the way a drop on the window does, and pumps frames until it is on screen.</summary>
    internal async Task OpenAsync(string path, CancellationToken ct)
    {
        Host.HandleDropFile(path);
        await PumpUntilAsync(() => IsShowing(path), $"{Path.GetFileName(path)} to open", ct);
    }

    /// <summary>Whether the controller's document is the file at <paramref name="path"/>.</summary>
    internal bool IsShowing(string path)
        => Controller.Document is { } document
            && string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs frames until <paramref name="done"/> holds and no load is in flight. A document loads on the
    /// thread pool, so this waits in real time between frames; the bound is generous because a loaded test
    /// machine is slow, and giving up throws with the status line, which is where a failed open says why.
    /// </summary>
    internal async Task PumpUntilAsync(Func<bool> done, string what, CancellationToken ct)
    {
        for (var i = 0; i < 1000; i++)
        {
            Frame();
            if (done() && !Controller.IsLoadPending)
            {
                // One more, so the frame a test inspects is the one drawn AFTER the swap.
                Frame();
                return;
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException($"gave up waiting for {what}; status: '{State.StatusMessage}'");
    }

    /// <summary>
    /// The window resized, as <c>Program.cs</c>'s resize callback does it (the surface, then the viewer's
    /// layout size), then a frame.
    /// </summary>
    internal void Resize(uint width, uint height)
    {
        _renderer.Resize(width, height);
        Viewer.Resize(width, height);
        Frame();
    }

    /// <summary>A left click: press and release through the host, then a frame.</summary>
    internal void Click(float x, float y, MouseButton button = MouseButton.Left)
    {
        Host.HandlePointer(new InputEvent.MouseDown(x, y, button));
        Host.HandlePointer(new InputEvent.MouseUp(x, y, button));
        Frame();
    }

    /// <summary>A click at the centre of a painted rect.</summary>
    internal void Click(RectF32 rect, MouseButton button = MouseButton.Left)
        => Click(rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f), button);

    /// <summary>A click on a toolbar button, which must be on the bar.</summary>
    internal void Click(ToolbarAction action, MouseButton button = MouseButton.Left)
        => Click(ToolbarButton(action), button);

    /// <summary>A key press and release through the host, then a frame.</summary>
    internal void Key(InputKey key, InputModifier modifiers = InputModifier.None)
    {
        Host.HandleKeyDown(new InputEvent.KeyDown(key, modifiers));
        Host.HandleKeyUp(new InputEvent.KeyUp(key, modifiers));
        Frame();
    }

    /// <summary>A drag: press, one move per step, release, then a frame.</summary>
    internal void Drag(float fromX, float fromY, float toX, float toY, int steps = 8)
    {
        Host.HandlePointer(new InputEvent.MouseDown(fromX, fromY));
        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            Host.HandlePointer(new InputEvent.MouseMove(fromX + ((toX - fromX) * t), fromY + ((toY - fromY) * t),
                MouseButton.Left));
        }

        Host.HandlePointer(new InputEvent.MouseUp(toX, toY));
        Frame();
    }

    /// <summary>Where a toolbar button was painted this frame.</summary>
    internal RectF32 ToolbarButton(ToolbarAction action)
    {
        Viewer.TryGetPaintedToolbarRect(action, out var rect).ShouldBeTrue($"{action} is on the toolbar and enabled");
        return rect;
    }

    /// <summary>The one painted region whose hit matches, as the inspector and a click would find it.</summary>
    internal RectF32 Region(Func<HitResult, bool> match, string what)
    {
        var regions = Viewer.PaintedRegions().Where(r => match(r.Result)).ToArray();
        regions.Length.ShouldBe(1, $"exactly one painted region is {what}");
        return new RectF32(regions[0].X, regions[0].Y, regions[0].Width, regions[0].Height);
    }

    public void Dispose()
    {
        _appCts.Cancel();
        _appCts.Dispose();
        _renderer.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A document still holding the file open on a slow machine. The folder is under the system
            // temp directory, so leaving it is harmless and failing the test over it would not be.
        }
    }

    /// <summary>The viewer on a CPU surface. Only the GPU-facing members are stubbed.</summary>
    internal sealed class Surface : ImageRendererBase<RgbaImage>
    {
        public Surface(RgbaImageRenderer renderer, SignalBus bus) : base(renderer)
        {
            Bus = bus;
            Width = renderer.Width;
            Height = renderer.Height;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels) { }

        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
            ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness) { }

        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
            RGBAColor32 color, float thickness) { }

        protected override void OnResize(uint width, uint height) { }

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) { }

        // No GPU histogram on a CPU surface, so the histogram panel and its LOG toggle are not drawn here.
        protected override HistogramDisplay? GetHistogramDisplay() => null;

        /// <summary>The picture's pane, where the pan and the context menu answer.</summary>
        internal RectF32 ImageArea => ImageAreaRect;
    }
}
