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
                chrome.SessionState, chrome.EquipmentState, chrome.LiveSessionState, bus, tracker, cts, external);

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
            InputEvent.MouseUp(_, _, _) => HandleMouseUp(),
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

            // If chrome didn't handle it, try the active tab
            if (hit is null)
            {
                hit = _chrome.ActiveTab?.HitTestAndDispatch(px, py, modifiers);
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

            // Slider drag start + selection
            if (hit is HitResult.SliderHit { SliderIndex: var sliderIdx })
            {
                _plannerState.DraggingSliderIndex = sliderIdx;
                PlannerActions.SelectSlider(_plannerState, sliderIdx);
                return true;
            }

            // Clicking outside a slider → deselect
            if (_plannerState.SelectedSliderIndex >= 0)
            {
                PlannerActions.SelectSlider(_plannerState, -1);
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

            target.InsertText(text);
            target.OnTextChanged?.Invoke(target.Text);
            return true;
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

            var idx = _plannerState.DraggingSliderIndex;
            if (idx < 0)
            {
                // Forward to active tab (e.g. live session drag pan)
                return _chrome.ActiveTab?.HandleInput(new InputEvent.MouseMove(px, py)) ?? false;
            }

            if (idx >= _plannerState.HandoffSliders.Count)
            {
                _plannerState.DraggingSliderIndex = -1;
                return false;
            }

            var chartRect = _chrome.PlannerChartRect;
            var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(
                _plannerState, (int)chartRect.X, (int)chartRect.Width);

            var newTime = AltitudeChartRenderer.XToTime(px, tStart, tEnd, plotX, plotW);
            PlannerActions.MoveSlider(_plannerState, idx, newTime);
            return true;
        }

        private bool HandleMouseUp()
        {
            // Forward to active tab first (e.g. live session drag pan release)
            _chrome.ActiveTab?.HandleInput(new InputEvent.MouseUp(0, 0));

            if (_plannerState.DraggingSliderIndex >= 0)
            {
                _plannerState.DraggingSliderIndex = -1;
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
            var activeInput = _appState.ActiveTextInput;
            if (activeInput is { IsActive: true })
            {
                return HandleTextInputKey(activeInput, inputKey, inputModifier);
            }

            return false; // Not consumed — let caller route to active tab
        }

        // ===================================================================
        // Text input handling — generic with callbacks
        // ===================================================================

        private void ActivateTextInput(TextInputState input)
            => _chrome.Bus?.Post(new ActivateTextInputSignal(input));

        private void DeactivateTextInput()
            => _chrome.Bus?.Post(new DeactivateTextInputSignal());

        private bool HandleTextInputKey(TextInputState activeInput, InputKey key, InputModifier modifiers)
        {
            // Autocomplete arrow navigation
            if (activeInput == _plannerState.SearchInput && _plannerState.Suggestions.Count > 0)
            {
                if (key == InputKey.Down)
                {
                    _plannerState.SuggestionIndex = Math.Min(
                        _plannerState.SuggestionIndex + 1, _plannerState.Suggestions.Count - 1);
                    _appState.NeedsRedraw = true;
                    return true;
                }

                if (key == InputKey.Up && _plannerState.SuggestionIndex >= 0)
                {
                    _plannerState.SuggestionIndex--;
                    _appState.NeedsRedraw = true;
                    return true;
                }
            }

            var textKey = key.ToTextInputKey(modifiers);

            // Tab cycling through text inputs on the active tab
            if (key == InputKey.Tab)
            {
                var shift = (modifiers & InputModifier.Shift) != 0;
                var inputs = _chrome.ActiveTab?.GetRegisteredTextInputs();
                if (inputs is { Count: > 1 } && _appState.ActiveTextInput is { } current)
                {
                    var idx = inputs.IndexOf(current);
                    if (idx >= 0)
                    {
                        var next = shift
                            ? inputs[(idx - 1 + inputs.Count) % inputs.Count]
                            : inputs[(idx + 1) % inputs.Count];
                        current.Deactivate();
                        next.Activate();
                        _appState.ActiveTextInput = next;
                        _appState.NeedsRedraw = true;
                        return true;
                    }
                }
            }

            // Let the input's OnKeyOverride handle it first (autocomplete, etc.)
            if (textKey.HasValue && activeInput.OnKeyOverride?.Invoke(textKey.Value) == true)
            {
                _appState.NeedsRedraw = true;
                return true;
            }

            // Handle Ctrl+V paste via platform clipboard
            if (textKey == TextInputKey.Paste)
            {
                var clipboardText = GetClipboardText?.Invoke();
                if (!string.IsNullOrEmpty(clipboardText))
                {
                    activeInput.InsertText(clipboardText);
                    activeInput.OnTextChanged?.Invoke(activeInput.Text);
                }
                _appState.NeedsRedraw = true;
                return true;
            }

            // Handle Ctrl+C copy via platform clipboard
            if (textKey == TextInputKey.Copy)
            {
                if (activeInput.HasSelection)
                {
                    SetClipboardText?.Invoke(activeInput.Text[activeInput.SelectionStart..activeInput.SelectionEnd]);
                }
                return true;
            }

            if (textKey.HasValue && activeInput.HandleKey(textKey.Value))
            {
                // Notify text change on modifying keys
                if (textKey.Value is TextInputKey.Backspace or TextInputKey.Delete)
                {
                    activeInput.OnTextChanged?.Invoke(activeInput.Text);
                }

                if (activeInput.IsCommitted)
                {
                    if (activeInput.OnCommit is { } onCommit)
                    {
                        var text = activeInput.Text;
                        _tracker.Run(() => onCommit(text), "Text input commit");
                    }
                    activeInput.IsCommitted = false;
                }
                else if (activeInput.IsCancelled)
                {
                    activeInput.OnCancel?.Invoke();
                    activeInput.IsCancelled = false;
                    DeactivateTextInput();
                }

                _appState.NeedsRedraw = true;
                return true;
            }

            return true; // Swallow all keys when text input active
        }
    }
}
