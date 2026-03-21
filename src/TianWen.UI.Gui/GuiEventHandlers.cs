using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using static SDL3.SDL;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Centralized event handling for the GUI. Owns all SDL-dependent and DI-dependent
    /// actions — tabs handle pure state mutations via OnClick delegates.
    /// </summary>
    public sealed class GuiEventHandlers
    {
        private readonly IServiceProvider _sp;
        private readonly GuiAppState _appState;
        private readonly PlannerState _plannerState;
        private readonly VkGuiRenderer _guiRenderer;
        private readonly nint _sdlWindowHandle;
        private readonly CancellationTokenSource _cts;
        private readonly IExternal _external;
        private readonly ILogger _logger;

        public string[]? AutoCompleteCache { get; set; }

        public GuiEventHandlers(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            VkGuiRenderer guiRenderer,
            nint sdlWindowHandle,
            CancellationTokenSource cts,
            IExternal external)
        {
            _sp = sp;
            _appState = appState;
            _plannerState = plannerState;
            _guiRenderer = guiRenderer;
            _sdlWindowHandle = sdlWindowHandle;
            _cts = cts;
            _external = external;
            _logger = external.AppLogger;
        }

        /// <summary>
        /// Handles a mouse click. Returns true if the event was consumed.
        /// </summary>
        public bool HandleMouseDown(float px, float py, byte clicks)
        {
            _appState.MouseScreenPosition = (px, py);

            // Hit test the GUI chrome (sidebar, status bar) first — OnClick handles tab switching
            var hit = _guiRenderer.HitTestAndDispatch(px, py);

            // Auto-discover on tab switch to Equipment
            if (hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("Tab:"))
            {
                if (action == "Tab:Equipment" && _guiRenderer.EquipmentTab.State.DiscoveredDevices.Count == 0)
                {
                    HandleEquipmentAction("Discover");
                }
                _appState.NeedsRedraw = true;
                return true;
            }

            // If chrome didn't handle it, try the active tab
            if (hit is null)
            {
                var currentTab = _guiRenderer.ActiveTab;
                hit = currentTab?.HitTestAndDispatch(px, py);
            }

            // Text input focus management (needs SDL StartTextInput/StopTextInput)
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
            if (_appState.ActiveTextInput is { IsActive: true } active && hit is not HitResult.TextInputHit)
            {
                DeactivateTextInput();
            }

            // Equipment tab actions that need DI/SDL
            if (_appState.ActiveTab is GuiTab.Equipment && hit is not null)
            {
                HandleEquipmentHit(hit);
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

            // Update autocomplete if this is the planner search input
            if (target == _plannerState.SearchInput && AutoCompleteCache is not null)
            {
                PlannerActions.UpdateSuggestions(_plannerState, AutoCompleteCache, target.Text);
            }
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

            var chartRect = _guiRenderer.PlannerTab.ChartRect;
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
        /// Handles mouse wheel scroll.
        /// </summary>
        public void HandleMouseWheel(float scrollY)
        {
            if (_appState.ActiveTab is GuiTab.Planner)
            {
                var pos = _appState.MouseScreenPosition;
                if (_guiRenderer.PlannerTab.TargetListRect.Contains(pos.X, pos.Y))
                {
                    _guiRenderer.PlannerTab.ScrollOffset = Math.Max(0,
                        _guiRenderer.PlannerTab.ScrollOffset - (int)scrollY * 3);
                    _plannerState.NeedsRedraw = true;
                }
            }
            else if (_appState.ActiveTab is GuiTab.Session)
            {
                var pos = _appState.MouseScreenPosition;
                var sessionTab = _guiRenderer.SessionTab;
                if (sessionTab.ConfigPanelRect.Contains(pos.X, pos.Y))
                {
                    sessionTab.State.ConfigScrollOffset = Math.Max(0,
                        sessionTab.State.ConfigScrollOffset - (int)(scrollY * sessionTab.ScrollLineHeight));
                    _appState.NeedsRedraw = true;
                }
            }
        }

        /// <summary>
        /// Handles a key down event. Returns true if consumed.
        /// </summary>
        public bool HandleKeyDown(Scancode scancode, Keymod keymod)
        {
            var activeInput = _appState.ActiveTextInput;
            if (activeInput is { IsActive: true })
            {
                return HandleTextInputKey(activeInput, scancode, keymod);
            }

            return false; // Not consumed — let caller handle global keys
        }

        /// <summary>
        /// Handles planner-specific keyboard shortcuts.
        /// </summary>
        public void HandlePlannerKey(Scancode scancode)
        {
            var tab = _guiRenderer.PlannerTab;
            var filtered = tab.FilteredTargets;

            switch (scancode)
            {
                case Scancode.Up:
                    if (_plannerState.SelectedTargetIndex > 0)
                    {
                        _plannerState.SelectedTargetIndex--;
                        tab.EnsureVisible(_plannerState.SelectedTargetIndex);
                        _plannerState.NeedsRedraw = true;
                    }
                    break;

                case Scancode.Down:
                    if (_plannerState.SelectedTargetIndex < filtered.Count - 1)
                    {
                        _plannerState.SelectedTargetIndex++;
                        tab.EnsureVisible(_plannerState.SelectedTargetIndex);
                        _plannerState.NeedsRedraw = true;
                    }
                    break;

                case Scancode.Return when _plannerState.SelectedTargetIndex >= 0 && _plannerState.SelectedTargetIndex < filtered.Count:
                    PlannerActions.ToggleProposal(_plannerState, filtered[_plannerState.SelectedTargetIndex].Target);
                    break;

                case Scancode.P when _plannerState.SelectedTargetIndex >= 0 && _plannerState.SelectedTargetIndex < filtered.Count:
                    var propIdx = _plannerState.Proposals.FindIndex(p => p.Target == filtered[_plannerState.SelectedTargetIndex].Target);
                    if (propIdx >= 0)
                    {
                        PlannerActions.CyclePriority(_plannerState, propIdx);
                    }
                    break;

                case Scancode.F:
                    PlannerActions.CycleRatingFilter(_plannerState);
                    _plannerState.SelectedTargetIndex = 0;
                    tab.ScrollOffset = 0;
                    break;

                case Scancode.M:
                    CycleMinAltitude();
                    break;

                case Scancode.S:
                    BuildScheduleFromProfile();
                    break;
            }
        }

        public void CommitSuggestion(string suggestion)
        {
            _plannerState.SearchInput.Text = suggestion;
            _plannerState.SearchInput.CursorPos = suggestion.Length;
            _plannerState.Suggestions.Clear();
            _plannerState.SuggestionIndex = -1;
            _plannerState.LastSuggestionQuery = suggestion;

            if (_appState.ActiveProfile is not null)
            {
                var transform = TransformFactory.FromProfile(_appState.ActiveProfile, _external.TimeProvider, out _);
                if (transform is not null)
                {
                    var db = _sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                    var resultIdx = PlannerActions.CommitSuggestion(_plannerState, db, transform, suggestion);
                    if (resultIdx >= 0)
                    {
                        _plannerState.SelectedTargetIndex = resultIdx;
                        _guiRenderer.PlannerTab.EnsureVisible(resultIdx);
                    }
                }
            }
            _appState.NeedsRedraw = true;
        }

        // --- Private helpers ---

        private void ActivateTextInput(TextInputState input)
        {
            if (_appState.ActiveTextInput is { } prev && prev != input)
            {
                prev.Deactivate();
            }
            input.Activate();
            _appState.ActiveTextInput = input;
            StartTextInput(_sdlWindowHandle);
            _appState.NeedsRedraw = true;
        }

        private void DeactivateTextInput()
        {
            if (_appState.ActiveTextInput is not { IsActive: true } active)
            {
                return;
            }

            active.Deactivate();
            _appState.ActiveTextInput = null;
            StopTextInput(_sdlWindowHandle);

            if (active == _plannerState.SearchInput)
            {
                _plannerState.Suggestions.Clear();
                _plannerState.SuggestionIndex = -1;
                _plannerState.LastSuggestionQuery = "";
            }
        }

        private bool HandleTextInputKey(TextInputState activeInput, Scancode scancode, Keymod keymod)
        {
            // Autocomplete navigation
            if (activeInput == _plannerState.SearchInput && _plannerState.Suggestions.Count > 0)
            {
                if (scancode == Scancode.Down)
                {
                    _plannerState.SuggestionIndex = Math.Min(
                        _plannerState.SuggestionIndex + 1, _plannerState.Suggestions.Count - 1);
                    _appState.NeedsRedraw = true;
                    return true;
                }

                if (scancode == Scancode.Up && _plannerState.SuggestionIndex >= 0)
                {
                    _plannerState.SuggestionIndex--;
                    _appState.NeedsRedraw = true;
                    return true;
                }

                if (scancode == Scancode.Return && _plannerState.SuggestionIndex >= 0)
                {
                    CommitSuggestion(_plannerState.Suggestions[_plannerState.SuggestionIndex]);
                    return true;
                }

                if (scancode == Scancode.Escape)
                {
                    _plannerState.Suggestions.Clear();
                    _plannerState.SuggestionIndex = -1;
                    _plannerState.LastSuggestionQuery = "";
                    _appState.NeedsRedraw = true;
                    return true;
                }
            }

            var inputKey = scancode switch
            {
                Scancode.Backspace => TextInputKey.Backspace,
                Scancode.Delete => TextInputKey.Delete,
                Scancode.Left => TextInputKey.Left,
                Scancode.Right => TextInputKey.Right,
                Scancode.Home => TextInputKey.Home,
                Scancode.End => TextInputKey.End,
                Scancode.Return => TextInputKey.Enter,
                Scancode.Escape => TextInputKey.Escape,
                Scancode.A when (keymod & Keymod.Ctrl) != 0 => TextInputKey.SelectAll,
                _ => (TextInputKey?)null
            };

            // Tab cycling through text inputs
            if (scancode == Scancode.Tab)
            {
                var shift = (keymod & Keymod.Shift) != 0;
                var inputs = _guiRenderer.EquipmentTab.GetRegisteredTextInputs();
                if (inputs.Count > 1 && _appState.ActiveTextInput is { } current)
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

            if (inputKey.HasValue && activeInput.HandleKey(inputKey.Value))
            {
                // Update suggestions on text-modifying keys
                if (activeInput == _plannerState.SearchInput && AutoCompleteCache is not null
                    && inputKey.Value is TextInputKey.Backspace or TextInputKey.Delete)
                {
                    PlannerActions.UpdateSuggestions(_plannerState, AutoCompleteCache, activeInput.Text);
                }

                if (activeInput.IsCommitted)
                {
                    HandleTextInputCommit(activeInput);
                }
                else if (activeInput.IsCancelled)
                {
                    HandleTextInputCancel(activeInput);
                }

                _appState.NeedsRedraw = true;
                return true;
            }

            return true; // Swallow all keys when text input active
        }

        private void HandleTextInputCommit(TextInputState input)
        {
            var eqState = _guiRenderer.EquipmentTab.State;

            if (input == _plannerState.SearchInput)
            {
                _plannerState.Suggestions.Clear();
                _plannerState.SuggestionIndex = -1;
                _plannerState.LastSuggestionQuery = "";

                if (_appState.ActiveProfile is not null && input.Text.Length > 0)
                {
                    var transform = TransformFactory.FromProfile(_appState.ActiveProfile, _external.TimeProvider, out _);
                    if (transform is not null)
                    {
                        var db = _sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        var resultIdx = PlannerActions.SearchTargets(_plannerState, db, transform, input.Text);
                        if (resultIdx >= 0)
                        {
                            _plannerState.SelectedTargetIndex = resultIdx;
                            _guiRenderer.PlannerTab.EnsureVisible(resultIdx);
                        }
                    }
                }
                input.IsCommitted = false;
            }
            else if (eqState.IsEditingSite)
            {
                HandleEquipmentAction("SaveSite");
            }
            else if (input.Text.Length > 0)
            {
                HandleEquipmentAction("CreateProfile");
            }
        }

        private void HandleTextInputCancel(TextInputState input)
        {
            var eqState = _guiRenderer.EquipmentTab.State;

            if (input == _plannerState.SearchInput)
            {
                _plannerState.SearchInput.Deactivate();
                _plannerState.SearchInput.Clear();
                _plannerState.SearchResults.Clear();
                _plannerState.Suggestions.Clear();
                _plannerState.SuggestionIndex = -1;
                _plannerState.LastSuggestionQuery = "";
                _appState.ActiveTextInput = null;
                StopTextInput(_sdlWindowHandle);
                _plannerState.NeedsRedraw = true;
            }
            else if (eqState.IsEditingSite)
            {
                eqState.IsEditingSite = false;
                eqState.LatitudeInput.Deactivate();
                eqState.LongitudeInput.Deactivate();
                eqState.ElevationInput.Deactivate();
                _appState.ActiveTextInput = null;
            }
            else
            {
                eqState.IsCreatingProfile = false;
                eqState.ProfileNameInput.Clear();
            }
            input.Deactivate();
            StopTextInput(_sdlWindowHandle);
        }

        private void HandleEquipmentHit(HitResult hit)
        {
            switch (hit)
            {
                case HitResult.ButtonHit { Action: var action }:
                    HandleEquipmentAction(action);
                    break;

                case HitResult.SlotHit { Slot: { } slot }:
                    var eqState = _guiRenderer.EquipmentTab.State;
                    eqState.ActiveAssignment = eqState.ActiveAssignment == slot ? null : slot;
                    _appState.NeedsRedraw = true;
                    break;

                case HitResult.ListItemHit { ListId: "Devices", Index: var deviceIndex }:
                    HandleDeviceAssignment(deviceIndex);
                    break;
            }
        }

        private void HandleEquipmentAction(string action)
        {
            var eqState = _guiRenderer.EquipmentTab.State;

            switch (action)
            {
                case "CreateProfile":
                    if (!eqState.IsCreatingProfile)
                    {
                        eqState.IsCreatingProfile = true;
                        eqState.ProfileNameInput.Activate();
                        _appState.ActiveTextInput = eqState.ProfileNameInput;
                        StartTextInput(_sdlWindowHandle);
                    }
                    else if (eqState.ProfileNameInput.Text.Length > 0)
                    {
                        var name = eqState.ProfileNameInput.Text;
                        _ = Task.Run(async () =>
                        {
                            var profile = await EquipmentActions.CreateProfileAsync(name, _external, _cts.Token);
                            _appState.ActiveProfile = profile;
                            eqState.IsCreatingProfile = false;
                            eqState.ProfileNameInput.Deactivate();
                            eqState.ProfileNameInput.Clear();
                            _appState.ActiveTextInput = null;
                            StopTextInput(_sdlWindowHandle);
                            _plannerState.NeedsRecompute = true;
                            _appState.NeedsRedraw = true;
                        });
                    }
                    break;

                case "Discover":
                    if (!eqState.IsDiscovering)
                    {
                        eqState.IsDiscovering = true;
                        _appState.StatusMessage = "Discovering devices...";
                        _appState.NeedsRedraw = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var dm = _sp.GetRequiredService<ICombinedDeviceManager>();
                                await dm.CheckSupportAsync(_cts.Token);
                                await dm.DiscoverAsync(_cts.Token);
                                eqState.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                                    .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                                    .SelectMany(dm.RegisteredDevices)
                                    .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Device discovery failed");
                                _appState.StatusMessage = "Discovery failed";
                            }
                            finally
                            {
                                eqState.IsDiscovering = false;
                                _appState.StatusMessage = null;
                                _appState.NeedsRedraw = true;
                            }
                        });
                    }
                    break;

                case "AddOta" when _appState.ActiveProfile is { } p:
                    var d = p.Data ?? ProfileData.Empty;
                    var newOta = new OTAData(
                        Name: $"Telescope #{d.OTAs.Length}",
                        FocalLength: 1000,
                        Camera: NoneDevice.Instance.DeviceUri,
                        Cover: null, Focuser: null, FilterWheel: null,
                        PreferOutwardFocus: null, OutwardIsPositive: null,
                        Aperture: null, OpticalDesign: OpticalDesign.Unknown);
                    var newD = EquipmentActions.AddOTA(d, newOta);
                    var updatedP = p.WithData(newD);
                    _ = Task.Run(async () =>
                    {
                        await updatedP.SaveAsync(_external, _cts.Token);
                        _appState.ActiveProfile = updatedP;
                        _appState.NeedsRedraw = true;
                    });
                    break;

                case "EditSite":
                    var st = _guiRenderer.EquipmentTab.State;
                    st.IsEditingSite = true;
                    if (_appState.ActiveProfile?.Data is { } pd)
                    {
                        var existingSite = EquipmentActions.GetSiteFromMount(pd.Mount);
                        if (existingSite.HasValue)
                        {
                            st.LatitudeInput.Text = existingSite.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            st.LatitudeInput.CursorPos = st.LatitudeInput.Text.Length;
                            st.LongitudeInput.Text = existingSite.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            st.LongitudeInput.CursorPos = st.LongitudeInput.Text.Length;
                            st.ElevationInput.Text = existingSite.Value.Elev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                            st.ElevationInput.CursorPos = st.ElevationInput.Text.Length;
                        }
                    }
                    st.LatitudeInput.Activate();
                    _appState.ActiveTextInput = st.LatitudeInput;
                    StartTextInput(_sdlWindowHandle);
                    break;

                case "SaveSite" when _appState.ActiveProfile is { } siteProfile:
                    var st2 = _guiRenderer.EquipmentTab.State;
                    if (double.TryParse(st2.LatitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLat) &&
                        double.TryParse(st2.LongitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLon))
                    {
                        double? sElev = double.TryParse(st2.ElevationInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : null;
                        var sData = siteProfile.Data ?? ProfileData.Empty;
                        var newSiteData = EquipmentActions.SetSite(sData, sLat, sLon, sElev);
                        var updatedSite = siteProfile.WithData(newSiteData);
                        // Update UI immediately, save in background
                        _appState.ActiveProfile = updatedSite;
                        st2.IsEditingSite = false;
                        st2.LatitudeInput.Deactivate();
                        st2.LongitudeInput.Deactivate();
                        st2.ElevationInput.Deactivate();
                        _appState.ActiveTextInput = null;
                        StopTextInput(_sdlWindowHandle);
                        _plannerState.NeedsRecompute = true;
                        _appState.NeedsRedraw = true;
                        _ = Task.Run(async () => await updatedSite.SaveAsync(_external, _cts.Token));
                    }
                    else
                    {
                        _appState.StatusMessage = "Invalid latitude or longitude";
                    }
                    break;
            }

            _appState.NeedsRedraw = true;
        }

        private void HandleDeviceAssignment(int deviceIndex)
        {
            var eqState = _guiRenderer.EquipmentTab.State;
            if (deviceIndex < 0 || deviceIndex >= eqState.DiscoveredDevices.Count)
            {
                return;
            }

            if (eqState.ActiveAssignment is { } target && _appState.ActiveProfile is { } profile)
            {
                var device = eqState.DiscoveredDevices[deviceIndex];

                // Type guard: only allow devices matching the slot's expected type
                if (device.DeviceType != target.ExpectedDeviceType)
                {
                    _appState.StatusMessage = $"Expected {target.ExpectedDeviceType}, got {device.DeviceType}";
                    return;
                }

                var data = profile.Data ?? ProfileData.Empty;

                // Remove from any existing slot first to prevent duplicates
                data = EquipmentActions.UnassignDevice(data, device.DeviceUri);

                var newData = target switch
                {
                    AssignTarget.ProfileLevel { Field: "Mount" } => EquipmentActions.AssignMount(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "Guider" } => EquipmentActions.AssignGuider(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "GuiderCamera" } => EquipmentActions.AssignGuiderCamera(data, device.DeviceUri),
                    AssignTarget.ProfileLevel { Field: "GuiderFocuser" } => EquipmentActions.AssignGuiderFocuser(data, device.DeviceUri),
                    AssignTarget.OTALevel otaTarget => EquipmentActions.AssignDeviceToOTA(data, otaTarget.OtaIndex,
                        device.DeviceType, device.DeviceUri),
                    _ => data
                };

                var updated = profile.WithData(newData);
                // Update UI immediately (optimistic), save to disk in background
                _appState.ActiveProfile = updated;
                eqState.ActiveAssignment = null;
                _appState.NeedsRedraw = true;
                _ = Task.Run(async () => await updated.SaveAsync(_external, _cts.Token));
            }
        }

        private void CycleMinAltitude()
        {
            _plannerState.MinHeightAboveHorizon = _plannerState.MinHeightAboveHorizon switch
            {
                15 => 20, 20 => 25, 25 => 30, 30 => 35, _ => 15
            };

            if (_appState.ActiveProfile is not null)
            {
                var transform = TransformFactory.FromProfile(_appState.ActiveProfile, _external.TimeProvider, out _);
                if (transform is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        var db = _sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        await PlannerActions.ComputeTonightsBestAsync(_plannerState, db, transform,
                            _plannerState.MinHeightAboveHorizon, _cts.Token);
                    });
                }
            }
            _plannerState.NeedsRedraw = true;
        }

        private void BuildScheduleFromProfile()
        {
            if (_appState.ActiveProfile is null) return;
            var transform = TransformFactory.FromProfile(_appState.ActiveProfile, _external.TimeProvider, out _);
            if (transform is not null)
            {
                PlannerActions.BuildSchedule(_plannerState, transform,
                    defaultGain: 120, defaultOffset: 10,
                    defaultSubExposure: TimeSpan.FromSeconds(120),
                    defaultObservationTime: TimeSpan.FromMinutes(60));
            }
        }
    }
}
