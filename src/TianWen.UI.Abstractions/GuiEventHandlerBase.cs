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
                chrome.EquipmentState, bus, cts, external);

            // Wire the ensure-visible callback to the pixel widget's scroll mechanism
            _signalHandler.OnPlannerEnsureVisible = index => chrome.PlannerEnsureVisible(index);
        }

        /// <summary>Set by Program.cs after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache => _signalHandler.SetAutoCompleteCache;

        /// <summary>The shared signal handler for direct access by hosts.</summary>
        public AppSignalHandler SignalHandler => _signalHandler;

        // ===================================================================
        // Event routing — generic, no tab-specific logic
        // ===================================================================

        /// <summary>
        /// Handles a mouse click. Returns true if the event was consumed.
        /// </summary>
        public bool HandleMouseDown(float px, float py, byte clicks = 1)
        {
            _appState.MouseScreenPosition = (px, py);

            // Hit test the GUI chrome (sidebar, status bar) first — OnClick handles tab switching
            var hit = _chrome.HitTestAndDispatch(px, py);

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
                hit = _chrome.ActiveTab?.HitTestAndDispatch(px, py);
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

            // Slider drag start
            if (hit is HitResult.SliderHit { SliderIndex: var sliderIdx })
            {
                _plannerState.DraggingSliderIndex = sliderIdx;
                _appState.NeedsRedraw = true;
                return true;
            }

            // Clicking outside text input → deactivate
            if (_appState.ActiveTextInput is { IsActive: true } && hit is not HitResult.TextInputHit)
            {
                DeactivateTextInput();
            }

            _appState.NeedsRedraw = true;
            return hit is not null;
        }

        /// <summary>
        /// Handles a text character input event.
        /// </summary>
        public void HandleTextInput(string text)
        {
            if (_appState.ActiveTextInput is not { IsActive: true } target)
            {
                return;
            }

            target.InsertText(text);
            target.OnTextChanged?.Invoke(target.Text);
        }

        /// <summary>
        /// Handles mouse motion — updates slider position during drag.
        /// </summary>
        public void HandleMouseMove(float px, float py)
        {
            _appState.MouseScreenPosition = (px, py);

            if (_plannerState.DraggingSliderIndex < 0)
            {
                return;
            }

            var idx = _plannerState.DraggingSliderIndex;
            if (idx >= _plannerState.HandoffSliders.Count)
            {
                _plannerState.DraggingSliderIndex = -1;
                return;
            }

            var chartRect = _chrome.PlannerChartRect;
            var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(
                _plannerState, (int)chartRect.X, (int)chartRect.Width);

            var newTime = AltitudeChartRenderer.XToTime(px, tStart, tEnd, plotX, plotW);
            var minSlot = TimeSpan.FromMinutes(15);

            // Clamp between adjacent sliders (or dark/twilight boundaries)
            var minTime = idx > 0 ? _plannerState.HandoffSliders[idx - 1] + minSlot : _plannerState.AstroDark + minSlot;
            var maxTime = idx < _plannerState.HandoffSliders.Count - 1
                ? _plannerState.HandoffSliders[idx + 1] - minSlot
                : _plannerState.AstroTwilight - minSlot;

            if (newTime < minTime) newTime = minTime;
            if (newTime > maxTime) newTime = maxTime;

            _plannerState.HandoffSliders[idx] = newTime;
            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// Handles mouse button release — ends slider drag.
        /// </summary>
        public void HandleMouseUp()
        {
            if (_plannerState.DraggingSliderIndex >= 0)
            {
                _plannerState.DraggingSliderIndex = -1;
                _appState.NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Handles mouse wheel scroll — delegates to the active tab.
        /// </summary>
        public void HandleMouseWheel(float scrollY)
        {
            var pos = _appState.MouseScreenPosition;
            if (_chrome.ActiveTab?.HandleMouseWheel(scrollY, pos.X, pos.Y) == true)
            {
                _appState.NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Handles a key down event. Returns true if consumed by the text input layer.
        /// </summary>
        public bool HandleKeyDown(InputKey inputKey, InputModifier inputModifier)
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
