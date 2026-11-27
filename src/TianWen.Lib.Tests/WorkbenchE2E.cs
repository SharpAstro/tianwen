using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-edit</c> without its window: the real <see cref="WorkbenchHost{TSurface}"/>, the real
/// <see cref="WorkbenchRunner"/> asked off the render thread, the real <see cref="ViewerController"/> opening the original
/// through the real <see cref="DocumentCache"/>, and the real viewer painting onto a CPU surface. Input goes in the way SDL
/// hands it to <c>Program.cs</c> and a frame runs the loop's own steps in the loop's own order, as <see cref="ViewerE2E"/> does
/// for <c>tianwen-fits</c>.
/// </summary>
/// <remarks>
/// What is not here is what cannot be: SDL's event pump, the Vulkan draw, the window. Positions are read off what the layout
/// painted (the regions and the text leaves it arranged), never summed from constants, and the clipboard is a recorder.
/// </remarks>
internal sealed class WorkbenchE2E : IAsyncDisposable
{
    private readonly RgbaImageRenderer _renderer;
    private readonly CancellationTokenSource _appCts = new();

    private WorkbenchE2E(uint width, uint height, float dpiScale, WorkbenchRunner runner, OperationCatalog catalog)
    {
        _renderer = new RgbaImageRenderer(width, height);
        Bus = new SignalBus();
        ViewerState = new ViewerState();
        Tracker = new BackgroundTaskTracker();
        Clock = new FakeTimeProviderWrapper();
        FileDialog = Substitute.For<IFileDialogs>();
        Controller = new ViewerController(ViewerState, new DocumentCache(), FileDialog, Substitute.For<IPlateSolverFactory>(),
            Clock, Tracker, NullLogger<ViewerController>.Instance, external: null);
        Viewer = new RetainingSurface(_renderer, Bus) { DpiScale = dpiScale };
        // The app draws with its bundled face, not the machine's: the marks, the widths and the ellipsis points of a capture are
        // then the app's own (the test host bundles it under TestFonts, as the apps do under Fonts).
        var face = Path.Combine(AppContext.BaseDirectory, "TestFonts", "DejaVuSans.ttf");
        var emoji = Path.Combine(AppContext.BaseDirectory, "TestFonts", "NotoEmoji-Web.ttf");
        Viewer.FontPath = face;
        Viewer.EmojiFontPath = File.Exists(emoji) ? emoji : string.Empty;
        Viewer.FontFallback = FontFallbackResolver.FromRoles(face, emojiFontPath: File.Exists(emoji) ? emoji : null, scriptFontPaths: []);
        State = new WorkbenchState();
        Actions = new WorkbenchActions(State, runner, catalog, Tracker, FileDialog, Clipboard.Add,
            NullLogger.Instance, _appCts.Token);
        Host = new WorkbenchHost<RgbaImage>(_renderer, Viewer, ViewerState, Controller, State, Actions, Tracker, Bus,
            NullLogger.Instance, _appCts.Token);
        Host.Resize(width, height);
    }

    /// <summary>A window the size of an ordinary one at the given DPI scale, with no project open.</summary>
    internal static WorkbenchE2E Start(float dpiScale, WorkbenchRunner runner, OperationCatalog catalog, uint width = 1600, uint height = 1000)
    {
        var e2e = new WorkbenchE2E(width, height, dpiScale, runner, catalog);
        e2e.Frame();
        return e2e;
    }

    internal WorkbenchHost<RgbaImage> Host { get; }

    internal WorkbenchState State { get; }

    internal WorkbenchActions Actions { get; }

    internal ViewerState ViewerState { get; }

    internal ViewerController Controller { get; }

    internal RetainingSurface Viewer { get; }

    internal BackgroundTaskTracker Tracker { get; }

    internal SignalBus Bus { get; }

    internal FakeTimeProviderWrapper Clock { get; }

    internal IFileDialogs FileDialog { get; }

    /// <summary>What the host put on the clipboard, in order.</summary>
    internal List<string> Clipboard { get; } = [];

    /// <summary>The window's pixels as last painted.</summary>
    internal RgbaImageRenderer Renderer => _renderer;

    /// <summary>One iteration of the loop, in <c>Program.cs</c>'s order, drawn whatever the redraw question answers.</summary>
    internal void Frame()
    {
        _ = Host.WantsFrame();
        Host.BeforeFrame();
        _renderer.Surface.Clear(new RGBAColor32(0, 0, 0, 255));
        Host.Paint();
        Host.PaintTooltip();
        Host.AfterFrame();
    }

    /// <summary>
    /// Runs frames until <paramref name="done"/> holds and no background work is left. Work runs on the pool, so this waits
    /// between frames, for as long as the test's own <c>Timeout</c> allows (its token) and no longer; giving up names what it
    /// waited for.
    /// </summary>
    internal async Task PumpUntilAsync(Func<bool> done, string what, CancellationToken ct)
    {
        while (true)
        {
            Frame();
            if (done() && !Host.IsBusy)
            {
                // One more, so the frame a test inspects is the one drawn after the answer was adopted.
                Frame();
                return;
            }

            try
            {
                await Task.Delay(10, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new TimeoutException($"gave up waiting for {what}; status: '{ViewerState.StatusMessage}'; the viewer holds "
                    + $"'{Controller.Document?.FilePath}', is asked for '{ViewerState.RequestedFilePath}', loading: {Controller.IsLoadPending}, "
                    + $"texture update pending: {ViewerState.NeedsTextureUpdate}; the screen wants {Actions.Wanted()}");
            }
        }
    }

    /// <summary>
    /// Runs frames until <paramref name="done"/> holds, whatever work is still going: for a state that is only there while a run
    /// is, such as a step waiting at its gate. As <see cref="PumpUntilAsync"/> it waits between frames only as long as the test's
    /// token allows.
    /// </summary>
    internal async Task PumpFramesUntilAsync(Func<bool> done, string what, CancellationToken ct)
    {
        while (true)
        {
            Frame();
            if (done())
            {
                Frame();
                return;
            }

            try
            {
                await Task.Delay(10, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new TimeoutException($"gave up waiting for {what}");
            }
        }
    }

    /// <summary>A viewer that keeps what a before-pixels request asks it to, as the GPU viewer does, and counts the retentions.</summary>
    internal sealed class RetainingSurface(RgbaImageRenderer renderer, SignalBus bus) : ViewerE2E.Surface(renderer, bus)
    {
        private bool _retained;

        /// <summary>How often the viewer was asked to keep the pixels on show as the before half.</summary>
        internal int Retentions { get; private set; }

        public override bool HasBeforeImageTextures => _retained;

        // The default reads the machine-wide GC memory load, so a run of many classes on a loaded machine declines the
        // retention and the comparison never starts; this surface always keeps, as ViewerSplitTests pins it too.
        protected override bool ShouldSkipBeforePixelCache(long bytes) => Declines;

        /// <summary>True to play a machine short of memory: the viewer then declines to keep the before pixels.</summary>
        internal bool Declines { get; set; }

        public override bool TryRetainImageTexturesAsBefore()
        {
            _retained = true;
            Retentions++;
            return true;
        }

        public override void ReleaseBeforeImageTextures() => _retained = false;
    }

    /// <summary>Opens a project folder and pumps frames until the runner's answer is on screen.</summary>
    internal async Task OpenProjectAsync(string folder, CancellationToken ct, string? image = null)
    {
        Host.OpenProject(folder, image);
        await PumpUntilAsync(() => State.View is not null && !State.IsReading, "the project's status", ct);
    }

    /// <summary>A left click: press and release through the host, then a frame.</summary>
    internal void Click(RectF32 rect)
    {
        var (x, y) = (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));
        Host.HandlePointer(new InputEvent.MouseDown(x, y, MouseButton.Left));
        Host.HandlePointer(new InputEvent.MouseUp(x, y, MouseButton.Left));
        Frame();
    }

    /// <summary>A left click in the middle of a painted region.</summary>
    internal void Click(ClickableRegion region) => Click(new RectF32(region.X, region.Y, region.Width, region.Height));

    /// <summary>Clicks where the last frame painted this text, which a button or a choice declares as its label.</summary>
    internal void ClickText(string text)
    {
        var found = Texts().Where(t => t.Text == text).ToArray();
        if (found.Length != 1)
        {
            throw new InvalidOperationException($"{found.Length} painted texts are '{text}', not one");
        }

        Click(found[0].Bounds);
    }

    /// <summary>Every clickable region of the last frame.</summary>
    internal IReadOnlyList<ClickableRegion> Regions() => Host.Screen.PaintedRegions();

    /// <summary>The one region the last frame painted for a button of this action, as a click would find it.</summary>
    internal RectF32 Region(string action)
    {
        var found = Regions().Where(r => r.Result is HitResult.ButtonHit hit && hit.Action == action).ToArray();
        if (found.Length != 1)
        {
            throw new InvalidOperationException($"{found.Length} painted regions are '{action}', not one");
        }

        return new RectF32(found[0].X, found[0].Y, found[0].Width, found[0].Height);
    }

    /// <summary>The one button region of the last frame that holds this label, as a click would find it.</summary>
    internal ClickableRegion RegionOfLabel(string label)
    {
        var text = Texts().Single(t => t.Text == label).Bounds;
        var (cx, cy) = (text.X + (text.Width / 2f), text.Y + (text.Height / 2f));
        return Regions().Single(r => r.Result is HitResult.ButtonHit && cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height);
    }

    /// <summary>Answers the next folder dialogs in order: a path is a pick, null is a cancel. Further asks are cancelled.</summary>
    internal void FakeFolderPicks(params string?[] picks)
    {
        var next = 0;
        FileDialog.OpenFolderAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            var pick = next < picks.Length ? picks[next++] : null;
            return Task.FromResult(pick is null ? new FileDialogResult() : new FileDialogResult { Paths = [pick] });
        });
    }

    /// <summary>The rows of the open dropdown menu, in its order, as painted; empty while no menu is open.</summary>
    internal ImmutableArray<RectF32> MenuRows()
        => [.. Regions().Where(static r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId })
            .OrderBy(static r => ((HitResult.ListItemHit)r.Result).Index).Select(static r => new RectF32(r.X, r.Y, r.Width, r.Height))];

    /// <summary>The step cells of the step bar, left to right, as painted.</summary>
    internal ImmutableArray<RectF32> StepCells()
        => [.. Regions().Where(r => r.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("Step:", StringComparison.Ordinal))
            .OrderBy(static r => r.X).Select(static r => new RectF32(r.X, r.Y, r.Width, r.Height))];

    /// <summary>Every text leaf the last frame painted, with where it was arranged.</summary>
    internal ImmutableArray<(string Text, RectF32 Bounds)> Texts()
    {
        var nodes = new List<Layout.ArrangedNode<float>>();
        Host.Screen.CollectPaintedNodes(nodes);
        return [.. nodes.Where(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(static n => (((Layout.Content.Text)((Layout.Node.Leaf)n.Node).Content).Value,
                new RectF32(n.Bounds.X, n.Bounds.Y, n.Bounds.Width, n.Bounds.Height)))];
    }

    /// <summary>Every text leaf the last frame painted with the colour its author gave the glyphs.</summary>
    internal ImmutableArray<(string Text, RectF32 Bounds, RGBAColor32 Ink)> TextInks()
    {
        var nodes = new List<Layout.ArrangedNode<float>>();
        Host.Screen.CollectPaintedNodes(nodes);
        return [.. nodes.Where(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(static n =>
            {
                var text = (Layout.Content.Text)((Layout.Node.Leaf)n.Node).Content;
                return (text.Value, new RectF32(n.Bounds.X, n.Bounds.Y, n.Bounds.Width, n.Bounds.Height), text.Color);
            })];
    }

    /// <summary>The text leaves whose box lies inside <paramref name="rect"/>, left to right.</summary>
    internal IEnumerable<string> TextsIn(RectF32 rect)
        => Texts().Where(t => t.Bounds.X >= rect.X - 0.5f && t.Bounds.Y >= rect.Y - 0.5f
                && t.Bounds.X + t.Bounds.Width <= rect.X + rect.Width + 0.5f && t.Bounds.Y + t.Bounds.Height <= rect.Y + rect.Height + 0.5f)
            .OrderBy(static t => t.Bounds.Y).ThenBy(static t => t.Bounds.X).Select(static t => t.Text);

    /// <summary>The words painted in the step panel, in reading order, joined by a space.</summary>
    internal string PanelText()
    {
        var canvas = Host.Screen.CanvasRect;
        var cells = StepCells();
        var top = cells.IsEmpty ? 0f : cells.Max(static c => c.Y + c.Height);
        return string.Join(' ', TextsIn(new RectF32(0, top, canvas.X, _renderer.Height - top)));
    }

    public async ValueTask DisposeAsync()
    {
        _appCts.Cancel();
        await Controller.ShutdownAsync();
        _appCts.Dispose();
        _renderer.Dispose();
    }
}
