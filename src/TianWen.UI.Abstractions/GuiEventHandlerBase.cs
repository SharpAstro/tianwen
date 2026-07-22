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
        private readonly BackgroundTaskTracker _tracker;
        private readonly AppSignalHandler _signalHandler;

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
            _tracker = tracker;

            var bus = chrome.Bus!;

            // Create shared signal handler (all business logic)
            _signalHandler = new AppSignalHandler(sp, appState, plannerState,
                chrome.SessionState, chrome.EquipmentState, chrome.LiveSessionState,
                chrome.SkyMapState, bus, tracker, cts, shutdownToken, external);

            // Wire the ensure-visible callback to the pixel widget's scroll mechanism
            _signalHandler.OnPlannerEnsureVisible = index => chrome.PlannerEnsureVisible(index);
        }

        /// <summary>Set by Program.cs after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache => _signalHandler.SetAutoCompleteCache;

        /// <summary>The shared signal handler for direct access by hosts.</summary>
        public AppSignalHandler SignalHandler => _signalHandler;

        // ===================================================================
        // Unified input routing
        // ===================================================================

        /// <summary>
        /// Routes an input event through the GUI chrome and tab system.
        /// Returns true if the event was consumed.
        /// </summary>
        public bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.KeyDown(var key, var modifiers) => HandleKeyDown(key, modifiers),
            InputEvent.MouseDown(var px, var py, _, var mods, var clicks) => HandleMouseDown(px, py, mods, (byte)clicks),
            InputEvent.MouseMove(var px, var py) => HandleMouseMove(px, py),
            InputEvent.MouseUp(var upX, var upY, _) => HandleMouseUp(upX, upY),
            InputEvent.Scroll(var scrollY, _, _, _) => HandleMouseWheel(scrollY),
            InputEvent.TextInput(var text) => HandleTextInput(text),
            InputEvent.Pinch or InputEvent.PinchEnd => _chrome.ActiveTab?.HandleInput(evt) ?? false,
            _ => false
        };

        // ===================================================================
        // Event routing — generic, no tab-specific logic
        // ===================================================================

        private bool HandleMouseDown(float px, float py, InputModifier modifiers = InputModifier.None, byte clicks = 1)
        {
            _appState.MouseScreenPosition = (px, py);

            // Hit test the GUI chrome (sidebar, status bar) first — OnClick handles tab switching
            var hit = _chrome.HitTestAndDispatch(px, py, modifiers);

            // Auto-discover on tab switch to Equipment
            if (hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("Tab:"))
            {
                if (action == "Tab:Equipment" && _chrome.EquipmentState.DiscoveredDevices.Count == 0)
                {
                    _chrome.Bus?.Post(new DiscoverDevicesSignal());
                }
                _appState.NeedsRedraw = true;
                return true;
            }

            // A self-dispatching widget (the shared image viewer) owns its hit-testing + position-aware
            // dispatch: its toolbar buttons and WB/wavelet/transport sliders need the press X/Y, which an
            // OnClick handler can't carry, so they run inside HandleInput (HandleViewerMouseDown), not as
            // per-region OnClicks. Route the raw press straight there instead of pre-dispatching + short-
            // circuiting via HitTestAndDispatch. The chrome (sidebar/status) already got first crack above.
            if (hit is null && _chrome.ActiveTab is ISelfDispatchingInputWidget)
            {
                if (_appState.ActiveTextInput is { IsActive: true })
                {
                    DeactivateTextInput();
                }
                var consumed = _chrome.ActiveTab.HandleInput(
                    new InputEvent.MouseDown(px, py, Modifiers: modifiers, ClickCount: clicks));
                _appState.NeedsRedraw = true;
                return consumed;
            }

            // If chrome didn't handle it, try the active tab
            if (hit is null)
            {
                hit = _chrome.ActiveTab?.HitTestAndDispatch(px, py, modifiers);
            }

            // A hyperlink (planner details -> Wikipedia): open it via the host. Centralized here so every
            // LinkHit behaves identically, with no per-region wiring. The desktop host subscribes to
            // OpenUrlSignal and opens the OS browser; on the web the DOM <a> handles the click before the
            // canvas ever sees it, so this path is desktop-only in practice (and a no-op with no subscriber).
            if (hit is HitResult.LinkHit { Url: var url })
            {
                _chrome.Bus?.Post(new OpenUrlSignal(url));
                _appState.NeedsRedraw = true;
                return true;
            }

            // Text input focus management (via ActivateTextInputSignal/DeactivateTextInputSignal)
            if (hit is HitResult.TextInputHit { Input: { } clickedInput })
            {
                ActivateTextInput(clickedInput);
                if (clicks >= 2 && clickedInput.Text.Length > 0)
                {
                    clickedInput.SelectAll();
                }
                return true;
            }

            // Handoff-slider drag start / click-to-place / deselect - shared with the web host
            // (replaces the old click-empty-chart-to-deselect). Click-to-place is gated on the
            // planner tab being active so a stray press elsewhere can't move a slider through a
            // stale chart rect.
            if (PlannerSliderInteraction.HandleMouseDown(
                    _plannerState, hit, _chrome.PlannerChartRect, px, py,
                    allowClickToPlace: _appState.ActiveTab == GuiTab.Planner))
            {
                _appState.NeedsRedraw = true;
                return true;
            }

            // Clicking outside text input → deactivate
            if (_appState.ActiveTextInput is { IsActive: true } && hit is not HitResult.TextInputHit)
            {
                DeactivateTextInput();
            }

            // If no clickable region was hit, forward to active tab for custom handling (e.g. drag pan)
            if (hit is null)
            {
                _chrome.ActiveTab?.HandleInput(new InputEvent.MouseDown(px, py, Modifiers: modifiers, ClickCount: clicks));
            }

            _appState.NeedsRedraw = true;
            return hit is not null;
        }

        private bool HandleTextInput(string text)
        {
            if (_appState.ActiveTextInput is not { IsActive: true } target)
            {
                return false;
            }

            return TextInputInteraction.HandleText(target, text);
        }

        private bool HandleMouseMove(float px, float py)
        {
            var prev = _appState.MouseScreenPosition;
            _appState.MouseScreenPosition = (px, py);

            // Trigger redraw when the mouse enters/leaves the sidebar zone (for hover highlighting).
            // Sidebar is always in the left ~52px * DpiScale; we use a fixed threshold since
            // exact pixel width is renderer-specific. Only redraw on zone transitions, not every pixel.
            const float sidebarThreshold = 60f; // generous to cover DPI scaling
            var wasSidebar = prev.X < sidebarThreshold;
            var isSidebar = px < sidebarThreshold;
            if (isSidebar || wasSidebar)
            {
                _appState.NeedsRedraw = true;
            }

            // Active slider drag consumes the move; otherwise forward to the active tab
            // (e.g. live session drag pan).
            if (PlannerSliderInteraction.HandleMouseMove(_plannerState, _chrome.PlannerChartRect, px))
            {
                return true;
            }

            return _chrome.ActiveTab?.HandleInput(new InputEvent.MouseMove(px, py)) ?? false;
        }

        private bool HandleMouseUp(float x, float y)
        {
            // Forward to active tab first (e.g. live session drag pan release, sky-map click-select)
            _chrome.ActiveTab?.HandleInput(new InputEvent.MouseUp(x, y));

            if (PlannerSliderInteraction.HandleMouseUp(_plannerState))
            {
                _appState.NeedsRedraw = true;
                return true;
            }
            return false;
        }

        private bool HandleMouseWheel(float scrollY)
        {
            var pos = _appState.MouseScreenPosition;
            if (_chrome.ActiveTab?.HandleInput(new InputEvent.Scroll(scrollY, pos.X, pos.Y)) == true)
            {
                _appState.NeedsRedraw = true;
                return true;
            }
            return false;
        }

        private bool HandleKeyDown(InputKey inputKey, InputModifier inputModifier)
        {
            // F3 is a global shortcut (open sky-map search). Let it fall through to
            // the active tab even when a text input is focused — otherwise users would
            // have to click off the planner search before F3 would work.
            if (inputKey == InputKey.F3)
            {
                return false;
            }

            // Ctrl + letter tab shortcuts: global, bypass the active text input so
            // users don't have to defocus search fields to switch tabs.
            if ((inputModifier & InputModifier.Ctrl) != 0 && TrySwitchTabByShortcut(inputKey))
            {
                return true;
            }

            // Ctrl+Tab / Ctrl+Shift+Tab cycle through tabs in sidebar order (wraps around).
            // Also global so it works while a search field is focused.
            if (inputKey == InputKey.Tab && (inputModifier & InputModifier.Ctrl) != 0)
            {
                CycleTab(forward: (inputModifier & InputModifier.Shift) == 0);
                return true;
            }

            var activeInput = _appState.ActiveTextInput;
            if (activeInput is { IsActive: true })
            {
                return HandleTextInputKey(activeInput, inputKey, inputModifier);
            }

            return false; // Not consumed — let caller route to active tab
        }

        /// <summary>
        /// Ctrl+E/P/S/L/M/G/Y/N tab shortcuts. M = Sky Map, Y = Planetary, the rest map by first letter.
        /// </summary>
        private bool TrySwitchTabByShortcut(InputKey key)
        {
            GuiTab? target = key switch
            {
                InputKey.E => GuiTab.Equipment,
                InputKey.P => GuiTab.Planner,
                InputKey.S => GuiTab.Session,
                InputKey.L => GuiTab.LiveSession,
                InputKey.M => GuiTab.SkyMap,
                InputKey.G => GuiTab.Guider,
                InputKey.N => GuiTab.Notifications,
                _ => null
            };
            if (target is not { } tab || _appState.ActiveTab == tab) return false;
            _appState.ActiveTab = tab;
            if (tab == GuiTab.Notifications)
            {
                _appState.UnreadNotificationCount = 0;
            }
            _appState.NeedsRedraw = true;
            return true;
        }

        /// <summary>
        /// Cycles the active tab one step through <see cref="GuiAppState.TabOrder"/> (Ctrl+Tab forward,
        /// Ctrl+Shift+Tab backward), wrapping around at the ends.
        /// </summary>
        private void CycleTab(bool forward)
        {
            var tab = GuiAppState.NextTab(_appState.ActiveTab, forward);
            _appState.ActiveTab = tab;
            if (tab == GuiTab.Notifications)
            {
                _appState.UnreadNotificationCount = 0;
            }
            _appState.NeedsRedraw = true;
        }

        // ===================================================================
        // Text input handling — generic with callbacks
        // ===================================================================

        private void ActivateTextInput(TextInputState input)
            => _chrome.Bus?.Post(new ActivateTextInputSignal(input));

        private void DeactivateTextInput()
            => _chrome.Bus?.Post(new DeactivateTextInputSignal());

        // The key machinery lives in the host-agnostic TextInputInteraction (shared with the
        // web host); this supplies the desktop-flavoured context (signal-based focus release,
        // SDL clipboard delegates, GuiAppState redraw flag).
        private bool HandleTextInputKey(TextInputState activeInput, InputKey key, InputModifier modifiers)
        {
            return TextInputInteraction.HandleKey(activeInput, key, modifiers,
                new TextInputInteraction.KeyContext(
                    Tracker: _tracker,
                    Deactivate: DeactivateTextInput,
                    SetActive: next => _appState.ActiveTextInput = next,
                    RequestRedraw: () => _appState.NeedsRedraw = true,
                    Planner: _plannerState,
                    SkySearch: _chrome.SkyMapState.Search,
                    ActiveTab: _chrome.ActiveTab,
                    GetClipboardText: GetClipboardText,
                    SetClipboardText: SetClipboardText));
        }
    }
}
