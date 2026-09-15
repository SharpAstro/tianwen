using System;
using System.Threading;
using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Pixel-widget event routing for the GUI. Bridges input events to the
    /// widget/tab system via <see cref="IGuiChrome"/>. Business logic (signal
    /// subscriptions, text input callbacks) is handled by <see cref="AppSignalHandler"/>.
    /// </summary>
    public class GuiEventHandlerBase
    {
        private readonly GuiAppState _appState;
        private readonly PlannerState _plannerState;
        private readonly IGuiChrome _chrome;
        private readonly AppSignalHandler _signalHandler;
        private readonly InputRouter _router;

        // The frame, in paint order, for the router to walk top-most first. One entry: the chrome IS a
        // composite, so its own regions and every child's -- the navigation rail and the active tab --
        // come out of one CollectPaintedRegions walk in the order they were drawn.
        private readonly IPixelWidget[] _widgets;

        /// <summary>
        /// Platform-specific clipboard read callback (e.g. <c>SDL.GetClipboardText</c>).
        /// Set by the host application. When null, paste is a no-op.
        /// </summary>
        public Func<string?>? GetClipboardText { get; set; }

        /// <summary>
        /// Platform-specific clipboard write callback (e.g. <c>SDL.SetClipboardText</c>).
        /// Set by the host application. When null, copy is a no-op.
        /// </summary>
        public Action<string>? SetClipboardText { get; set; }

        public GuiEventHandlerBase(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            IGuiChrome chrome,
            CancellationTokenSource cts,
            CancellationToken shutdownToken,
            IExternal external,
            BackgroundTaskTracker tracker)
        {
            _appState = appState;
            _plannerState = plannerState;
            _chrome = chrome;

            // One focus owner per window, not two. The chrome's WindowUiSettings carries the instance the
            // widgets and the input router resolve focus against; the app state used to make its own, so
            // "which field has the keyboard" had two answers kept level by convention. Adopted here rather
            // than at construction of either, this being the first point that holds both.
            appState.AdoptWindowSettings(chrome.Ui);

            var bus = chrome.Bus
                ?? throw new ArgumentException("The chrome must carry a signal bus.", nameof(chrome));

            // Create shared signal handler (all business logic)
            _signalHandler = new AppSignalHandler(sp, appState, plannerState,
                chrome.SessionState, chrome.EquipmentState, chrome.ViewContexts,
                chrome.SkyMapState, bus, tracker, cts, shutdownToken, external);

            // Wire the ensure-visible callback to the pixel widget's scroll mechanism
            _signalHandler.OnPlannerEnsureVisible = index => chrome.PlannerEnsureVisible(index);

            _widgets = [chrome];
            _router = new InputRouter(chrome.Ui, tracker, () => _appState.NeedsRedraw = true)
            {
                Widgets = () => _widgets,
                Unhandled = RouteToApp,
                // Read through a lambda, not captured: both delegates are set by the host AFTER this
                // constructor returns, in the object initialiser that follows it.
                GetClipboardText = () => GetClipboardText?.Invoke(),
                SetClipboardText = text => SetClipboardText?.Invoke(text),
                ActiveSearch = ResolveActiveSearch,
            };

            // A hyperlink (planner details -> Wikipedia) opens through the host, which is what subscribes
            // to the signal: the desktop opens the OS browser, and on the web the DOM anchor has already
            // handled the click before the canvas sees it.
            _router.OpenUrl += url => chrome.Bus?.Post(new OpenUrlSignal(url));
        }

        /// <summary>Set by Program.cs after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache => _signalHandler.SetAutoCompleteCache;

        /// <summary>The shared signal handler for direct access by hosts.</summary>
        public AppSignalHandler SignalHandler => _signalHandler;

        // ===================================================================
        // Unified input routing
        // ===================================================================

        /// <summary>
        /// Routes an input event through the frame the last paint declared, and applies the one policy
        /// that is not a property of any node: which tab is on screen.
        /// Returns true if the event was consumed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The routing itself is <see cref="InputRouter"/>'s. What used to be here -- an overlay check, a
        /// hand-written Ctrl+letter map, an <c>if (key == F3) return false;</c> with a comment calling F3
        /// global, a press walk that hit-tested the chrome and then the tab, and a focused-field branch --
        /// is the same order on every surface, so it is stated once in the engine and the other hosts stop
        /// carrying their own copies of it.
        /// </para>
        /// <para>
        /// What stays is genuinely this host's: the pointer position the tabs read, the navigation rail's
        /// hover repaint, the planner's handoff-divider drag (which T2 deletes once a slider arms its own),
        /// and the tab policy below.
        /// </para>
        /// </remarks>
        public bool HandleInput(InputEvent evt)
        {
            switch (evt)
            {
                case InputEvent.MouseDown down:
                    _appState.MouseScreenPosition = (down.X, down.Y);
                    if (HandleSliderPress(down))
                    {
                        return true;
                    }
                    break;

                case InputEvent.MouseMove move:
                    NotePointerMoved(move.X, move.Y);
                    break;

                // The one binding with no node to sit on: Ctrl+Tab names no tab, it names the NEXT one, so
                // there is nothing painted for it to be a property of. Answered before the router because
                // TextInputInteraction reads a bare Tab as field cycling and would swallow it while a
                // search box has the keyboard, which is exactly the case this binding exists for.
                case InputEvent.KeyDown(InputKey.Tab, var mods) when (mods & InputModifier.Ctrl) != 0:
                    CycleTab(forward: (mods & InputModifier.Shift) == 0);
                    return true;
            }

            var before = _appState.ActiveTab;
            var consumed = _router.Handle(evt);
            if (_appState.ActiveTab != before)
            {
                ApplyTabPolicy(_appState.ActiveTab);
            }

            return consumed;
        }

        /// <summary>
        /// Call once the frame is painted: blurs a focused field that is no longer on screen, honours a
        /// field that asked for the keyboard as it appeared, and expires a tooltip whose region has gone.
        /// </summary>
        public void AfterPaint() => _router.AfterPaint();

        /// <summary>
        /// What the router did not claim. Keys go back to the host, which routes them to the active tab
        /// and then to its own global bindings; forwarding them here as well would run a tab's handler
        /// twice for every key it declines.
        /// </summary>
        private bool RouteToApp(InputEvent evt) => evt switch
        {
            InputEvent.KeyDown => false,
            InputEvent.MouseDown down => HandleMissedPress(down),
            InputEvent.MouseMove move => HandleMissedMove(move),
            InputEvent.MouseUp up => HandleMissedRelease(up),
            _ => _chrome.ActiveTab?.HandleInput(evt) ?? false,
        };

        /// <summary>
        /// A press that landed on no region at all: the active tab's own business (drag-pan, a scrollbar,
        /// the sky map's tap-vs-drag gesture).
        /// </summary>
        /// <remarks>
        /// The tab's own answer, for every tab. It used to be gated on an
        /// <c>ISelfDispatchingInputWidget</c> marker, which by this point had nothing left to mark: the
        /// interface meant "route the RAW press to this widget rather than pre-dispatching it", and the
        /// router made that true of every widget. All it still did was reshape the return value, and
        /// nothing reads it -- SdlWindowView discards what its press dispatch returns at all three call
        /// sites, and the browser host has no marker at all.
        /// </remarks>
        private bool HandleMissedPress(InputEvent.MouseDown down)
        {
            var consumed = _chrome.ActiveTab?.HandleInput(down) ?? false;
            _appState.NeedsRedraw = true;
            return consumed;
        }

        private bool HandleMissedMove(InputEvent.MouseMove move)
            // An active divider drag owns the move; the tab gets it otherwise (hover, drag-pan, scrollbar).
            => PlannerSliderInteraction.HandleMouseMove(_plannerState, _chrome.PlannerChartRect, move.X)
                || (_chrome.ActiveTab?.HandleInput(move) ?? false);

        private bool HandleMissedRelease(InputEvent.MouseUp up)
        {
            // The tab first (drag-pan release, sky-map click-select), then the divider drag, as before.
            _chrome.ActiveTab?.HandleInput(up);

            if (PlannerSliderInteraction.HandleMouseUp(_plannerState))
            {
                _appState.NeedsRedraw = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The planner's handoff-divider drag, which arms from a press and needs the press POSITION, so
        /// it is asked before the router dispatches, off a hit test that dispatches nothing.
        /// </summary>
        /// <remarks>
        /// Temporary, and the last thing in this file that is not routing: DIR.Lib 9.2's
        /// <c>Content.Slider</c> lets a slider arm its own drag from the node it painted, which is T2 and
        /// deletes both this and <see cref="PlannerSliderInteraction"/>'s three branches.
        /// </remarks>
        private bool HandleSliderPress(InputEvent.MouseDown down)
        {
            var probe = _chrome.HitTest(down.X, down.Y);
            if (probe is HitResult.TextInputHit)
            {
                return false;
            }

            if (!PlannerSliderInteraction.HandleMouseDown(
                    _plannerState, probe, _chrome.PlannerChartRect, down.X, down.Y,
                    allowClickToPlace: _appState.ActiveTab == GuiTab.Planner))
            {
                return false;
            }

            _appState.NeedsRedraw = true;
            return true;
        }

        /// <summary>
        /// Records where the pointer is and repaints when it crosses the navigation rail.
        /// </summary>
        /// <remarks>
        /// The rail resolves its own hover during PAINT, from the pointer the router hands every widget,
        /// so motion over it is a reason to draw a frame that nothing else can see: its cells carry a
        /// computed background rather than <c>Layout.Node.HoverBackground</c>, which is what the router
        /// watches. A fixed threshold, generous enough to cover any DPI scale, because the exact width is
        /// the renderer's.
        /// </remarks>
        private void NotePointerMoved(float px, float py)
        {
            const float sidebarThreshold = 60f;
            var wasSidebar = _appState.MouseScreenPosition.X < sidebarThreshold;
            _appState.MouseScreenPosition = (px, py);

            if (px < sidebarThreshold || wasSidebar)
            {
                _appState.NeedsRedraw = true;
            }
        }

        /// <summary>
        /// The result-list navigation the focused field is entitled to: the planner's autocomplete or the
        /// sky map's search, whichever box has the keyboard.
        /// </summary>
        private SearchInteraction? ResolveActiveSearch()
        {
            var active = _appState.ActiveTextInput;
            return active is null ? null
                : active == _plannerState.SearchInput ? _plannerState.Search
                : active == _chrome.SkyMapState.Search.SearchInput ? _chrome.SkyMapState.Search.Interaction
                : null;
        }

        /// <summary>
        /// What follows a tab becoming the visible one, whichever way it was reached: a press on the rail,
        /// its chord, or Ctrl+Tab.
        /// </summary>
        /// <remarks>
        /// Stated once, here, rather than on each route. The discovery kick used to ride on the press
        /// alone and the unread reset on the chord alone, so Ctrl+E reached an empty Equipment tab and a
        /// click on the bell left the badge lit.
        /// </remarks>
        private void ApplyTabPolicy(GuiTab tab)
        {
            if (tab == GuiTab.Equipment && _chrome.EquipmentState.DiscoveredDevices.Count == 0)
            {
                _chrome.Bus?.Post(new DiscoverDevicesSignal());
            }

            if (tab == GuiTab.Notifications)
            {
                _appState.UnreadNotificationCount = 0;
            }

            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// Cycles the active tab one step through <see cref="GuiAppState.TabOrder"/> (Ctrl+Tab forward,
        /// Ctrl+Shift+Tab backward), wrapping around at the ends.
        /// </summary>
        private void CycleTab(bool forward)
        {
            var tab = GuiAppState.NextTab(_appState.ActiveTab, forward);
            if (tab == _appState.ActiveTab)
            {
                return;
            }

            _appState.ActiveTab = tab;
            ApplyTabPolicy(tab);
        }

    }
}
