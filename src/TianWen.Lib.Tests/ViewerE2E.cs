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
using TianWen.Lib.Devices;

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
internal sealed class ViewerE2E : IAsyncDisposable
{
    internal const int ImageW = 64;
    internal const int ImageH = 48;

    private readonly RgbaImageRenderer _renderer;
    private readonly CancellationTokenSource _appCts = new CancellationTokenSource();
    private readonly TempFolders _folders = new TempFolders();
    private int _exitRequests;

    private ViewerE2E(uint width, uint height, float dpiScale, IExternal? external)
    {
        _renderer = new RgbaImageRenderer(width, height);
        Bus = new SignalBus();
        Bus.Subscribe<RequestExitSignal>(_ => _exitRequests++);
        State = new ViewerState();
        Tracker = new BackgroundTaskTracker();
        FileDialog = Substitute.For<IFileDialogs>();
        Clock = new FakeTimeProviderWrapper();
        Controller = new ViewerController(State, new DocumentCache(), FileDialog,
            Substitute.For<IPlateSolverFactory>(), Clock, Tracker,
            NullLogger<ViewerController>.Instance, external);
        // The bar tianwen-fits offers (Program.cs): the Enhance button is where the planetary sharpening lives (#1345).
        Viewer = new Surface(_renderer, Bus) { DpiScale = dpiScale, Offer = ToolbarOffer.FileViewer.With(ToolbarAction.Enhance) };
        Host = new StandaloneViewerHost<RgbaImage>(Viewer, State, Controller, Tracker, Bus,
            NullLogger.Instance, external, _appCts.Token);
        Folder = _folders.Create("tianwen-viewer-e2e-").FullName;
    }

    /// <summary>
    /// A viewer the size of an ordinary window at the given DPI scale, with nothing open. <paramref name="external"/> is the data root
    /// the viewer remembers things in (a file's display, the planetary telescope); null keeps nothing, as before.
    /// </summary>
    internal static ViewerE2E Start(float dpiScale, uint width = 1600, uint height = 1000, IExternal? external = null)
    {
        var e2e = new ViewerE2E(width, height, dpiScale, external);
        e2e.Frame();
        return e2e;
    }

    internal Surface Viewer { get; }

    /// <summary>The controller's clock, which a test advances to age what the viewer words by time.</summary>
    internal FakeTimeProviderWrapper Clock { get; }

    internal ViewerState State { get; }

    internal ViewerController Controller { get; }

    /// <summary>The substitute file dialog, which a test answers (a Save's path) through NSubstitute.</summary>
    internal IFileDialogs FileDialog { get; }

    internal BackgroundTaskTracker Tracker { get; }

    internal SignalBus Bus { get; }

    internal StandaloneViewerHost<RgbaImage> Host { get; }

    /// <summary>A scratch folder, deleted with the harness, for the files a test opens.</summary>
    internal string Folder { get; }

    /// <summary>How many times the app asked to exit.</summary>
    internal int ExitRequests => _exitRequests;

    /// <summary>
    /// One iteration of the loop, in <c>Program.cs</c>'s order: the redraw question, the between-frames step,
    /// the paint, the after-frame step. Drawn unconditionally, whatever <c>WantsFrame</c> answers: a frame
    /// nobody needed changes nothing, and skipping one a test did need would hide what it checks. But the
    /// question is ASKED, because asking it has effects the loop relies on (SER playback and the stacked
    /// view's stack are ticked there): without it a stacked view never built a master here.
    /// </summary>
    /// <remarks>
    /// Each frame starts from a cleared surface, as a whole frame on the GPU does (the render pass clears). Painted
    /// over the previous frame instead, a translucent piece of chrome converges a level a frame (the file list's
    /// divider did), and anything the previous frame drew and this one does not, a tooltip that has gone, stays
    /// on screen: neither is what the window shows. <see cref="ViewerRepaintResidueTests"/> is where a PARTIAL
    /// frame's preserved pixels are modelled.
    /// </remarks>
    internal void Frame()
    {
        _ = Host.WantsFrame();
        Host.BeforeFrame();
        _renderer.Surface.Clear(new RGBAColor32(0, 0, 0, 255));
        Viewer.Render(Controller.Source, State);
        Host.PaintTooltip();
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
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
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
    /// <paramref name="untilTimeout"/> drops the bound and waits for as long as the test's own <c>Timeout</c> allows (its token), for
    /// work whose length is the machine's rather than the viewer's: a derivation takes about 35 s in Debug, all of the bound.
    /// </summary>
    internal async Task PumpUntilAsync(Func<bool> done, string what, CancellationToken ct, bool untilTimeout = false)
    {
        for (var i = 0; untilTimeout || i < 1000; i++)
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
    {
        if (action is not ToolbarAction.Enhance)
        {
            CloseEnhancePopover();
        }

        Click(ToolbarButton(action), button);
    }

    /// <summary>Opens the Enhance popover by its button, as a user does, if it is not open; the planetary controls are in it.</summary>
    internal void OpenEnhancePopover()
    {
        if (!State.EnhancePopover.IsOpen)
        {
            Click(ToolbarAction.Enhance);
        }

        State.EnhancePopover.IsOpen.ShouldBeTrue("the Enhance button opens its popover");
    }

    /// <summary>The one painted region whose hit matches in the Enhance popover, which is opened first (#1345).</summary>
    internal RectF32 EnhanceRegion(Func<HitResult, bool> match, string what)
    {
        OpenEnhancePopover();
        return RegionOnShow(match, what);
    }

    /// <summary>Closes the Enhance popover as Escape does, if it is open.</summary>
    internal void CloseEnhancePopover()
    {
        if (State.EnhancePopover.IsOpen)
        {
            Key(InputKey.Escape);
            State.EnhancePopover.IsOpen.ShouldBeFalse("Escape closes the Enhance popover");
        }
    }

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

    /// <summary>The window's pixels as last painted, copied.</summary>
    internal byte[] Pixels() => [.. _renderer.Surface.Pixels];

    /// <summary>The window's width in pixels.</summary>
    internal int Width => _renderer.Surface.Width;

    /// <summary>Where a toolbar button was painted this frame.</summary>
    internal RectF32 ToolbarButton(ToolbarAction action)
    {
        Viewer.TryGetPaintedToolbarRect(action, out var rect).ShouldBeTrue($"{action} is on the toolbar and enabled");
        return rect;
    }

    /// <summary>The one painted region whose hit matches, as the inspector and a click would find it.</summary>
    internal RectF32 Region(Func<HitResult, bool> match, string what)
    {
        // A press outside an open popover only closes it, so a control that is not the popover's is reached with it closed.
        CloseEnhancePopover();
        return RegionOnShow(match, what);
    }

    private RectF32 RegionOnShow(Func<HitResult, bool> match, string what)
    {
        var regions = Viewer.PaintedRegions().Where(r => match(r.Result)).ToArray();
        regions.Length.ShouldBe(1, $"exactly one painted region is {what}");
        return new RectF32(regions[0].X, regions[0].Y, regions[0].Width, regions[0].Height);
    }

    /// <summary>
    /// Ends the viewer as <c>Program.cs</c> does at exit, <see cref="ViewerController.ShutdownAsync"/>, before the
    /// scratch folder goes: a SER source holds its file mapped until then, and the folder survived every delete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _appCts.Cancel();
        await Controller.ShutdownAsync();
        _appCts.Dispose();
        _renderer.Dispose();
        _folders.Dispose();
    }

    /// <summary>The viewer on a CPU surface. Only the GPU-facing members are stubbed. Not sealed, so a test can host it
    /// where a GUI hosts its viewer (the planetary view, which is one).</summary>
    internal class Surface : ImageRendererBase<RgbaImage>
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

        private HistogramDisplay? _histogram;

        /// <summary>
        /// Paint the histogram panel and its LOG toggle, from a display built the way the GPU viewer builds its own
        /// (<c>VkImageRenderer.UploadHistogramData</c>); the quad itself draws nothing here. Off by default, so a test that is
        /// not about the histogram keeps the image pane's top-right corner to itself.
        /// </summary>
        internal bool PaintsHistogram { get; set; }

        public override void UploadHistogramData(IPreviewSource source)
        {
            if (PaintsHistogram)
            {
                _histogram = new HistogramDisplay(source.ChannelStatistics);
            }
        }

        // No GPU histogram on a CPU surface unless a test asks for the panel (PaintsHistogram).
        protected override HistogramDisplay? GetHistogramDisplay() => PaintsHistogram ? _histogram : null;

        /// <summary>The picture's pane, where the pan and the context menu answer.</summary>
        internal RectF32 ImageArea => ImageAreaRect;

        /// <summary>The state the last render was given, for a host that keeps its own (the Live Session tab).</summary>
        internal ViewerState? State => CurrentViewerState;
    }
}
