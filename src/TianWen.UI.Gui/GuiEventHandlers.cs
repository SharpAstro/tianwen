using DIR.Lib;
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
    /// Centralized event handling for the GUI. Bridges SDL input events to the
    /// widget/tab system. Tab-specific logic lives in the tabs themselves via
    /// callbacks (<see cref="TextInputState.OnCommit"/>, <see cref="TextInputState.OnKeyOverride"/>,
    /// <see cref="IPixelWidget.HandleKeyDown"/>, <see cref="IPixelWidget.HandleMouseWheel"/>).
    /// </summary>
    public sealed class GuiEventHandlers
    {
        private readonly GuiAppState _appState;
        private readonly PlannerState _plannerState;
        private readonly VkGuiRenderer _guiRenderer;
        private readonly nint _sdlWindowHandle;
        private readonly BackgroundTaskTracker _tracker;

        public GuiEventHandlers(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            VkGuiRenderer guiRenderer,
            nint sdlWindowHandle,
            CancellationTokenSource cts,
            IExternal external,
            BackgroundTaskTracker tracker)
        {
            _appState = appState;
            _plannerState = plannerState;
            _guiRenderer = guiRenderer;
            _sdlWindowHandle = sdlWindowHandle;
            _tracker = tracker;

            var logger = external.AppLogger;
            guiRenderer.EquipmentTab.Tracker = tracker;

            // ---------------------------------------------------------------
            // Wire planner search input callbacks
            // ---------------------------------------------------------------
            string[]? autoCompleteCache = null;

            plannerState.SearchInput.OnCommit = text =>
            {
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";

                if (appState.ActiveProfile is not null && text.Length > 0)
                {
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
                    if (transform is not null)
                    {
                        var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        var resultIdx = PlannerActions.SearchTargets(plannerState, db, transform, text);
                        if (resultIdx >= 0)
                        {
                            plannerState.SelectedTargetIndex = resultIdx;
                            guiRenderer.PlannerTab.EnsureVisible(resultIdx);
                        }
                    }
                }
                return Task.CompletedTask;
            };

            plannerState.SearchInput.OnCancel = () =>
            {
                plannerState.SearchInput.Clear();
                plannerState.SearchResults.Clear();
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";
                appState.ActiveTextInput = null;
                StopTextInput(sdlWindowHandle);
                plannerState.NeedsRedraw = true;
            };

            plannerState.SearchInput.OnTextChanged = text =>
            {
                if (autoCompleteCache is not null)
                {
                    PlannerActions.UpdateSuggestions(plannerState, autoCompleteCache, text);
                }
            };

            // Autocomplete navigation: Up/Down/Return/Escape when suggestions are visible
            plannerState.SearchInput.OnKeyOverride = key =>
            {
                if (plannerState.Suggestions.Count == 0)
                {
                    return false;
                }

                switch (key)
                {
                    case TextInputKey.Backspace or TextInputKey.Delete:
                        return false; // Let the text input handle it, OnTextChanged will update suggestions

                    case TextInputKey.Enter when plannerState.SuggestionIndex >= 0:
                        CommitSuggestion(plannerState.Suggestions[plannerState.SuggestionIndex]);
                        return true;

                    case TextInputKey.Escape:
                        plannerState.Suggestions.Clear();
                        plannerState.SuggestionIndex = -1;
                        plannerState.LastSuggestionQuery = "";
                        appState.NeedsRedraw = true;
                        return true;

                    default:
                        return false;
                }

                // Down/Up are TextInputKey.Left/Right-adjacent but not in the enum.
                // They're handled via raw scancode below in HandleTextInputKey.
            };

            // ---------------------------------------------------------------
            // Wire equipment text input callbacks
            // ---------------------------------------------------------------
            var eqState = guiRenderer.EquipmentTab.State;

            eqState.ProfileNameInput.OnCommit = async text =>
            {
                if (text.Length > 0)
                {
                    var profile = await EquipmentActions.CreateProfileAsync(text, external, cts.Token);
                    appState.ActiveProfile = profile;
                    eqState.IsCreatingProfile = false;
                    eqState.ProfileNameInput.Deactivate();
                    eqState.ProfileNameInput.Clear();
                    appState.ActiveTextInput = null;
                    StopTextInput(sdlWindowHandle);
                    plannerState.NeedsRecompute = true;
                    appState.NeedsRedraw = true;
                }
            };

            eqState.ProfileNameInput.OnCancel = () =>
            {
                eqState.IsCreatingProfile = false;
                eqState.ProfileNameInput.Clear();
            };

            // Site inputs share a commit: save site on Enter from any of the three fields
            Func<Task> saveSite = async () =>
            {
                if (appState.ActiveProfile is not { } siteProfile)
                {
                    return;
                }

                if (double.TryParse(eqState.LatitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLat) &&
                    double.TryParse(eqState.LongitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLon))
                {
                    double? sElev = double.TryParse(eqState.ElevationInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : null;
                    var sData = siteProfile.Data ?? ProfileData.Empty;
                    var newSiteData = EquipmentActions.SetSite(sData, sLat, sLon, sElev);
                    var updatedSite = siteProfile.WithData(newSiteData);
                    // Update UI immediately, save in background
                    appState.ActiveProfile = updatedSite;
                    eqState.IsEditingSite = false;
                    eqState.LatitudeInput.Deactivate();
                    eqState.LongitudeInput.Deactivate();
                    eqState.ElevationInput.Deactivate();
                    appState.ActiveTextInput = null;
                    StopTextInput(sdlWindowHandle);
                    plannerState.NeedsRecompute = true;
                    appState.NeedsRedraw = true;
                    await updatedSite.SaveAsync(external, cts.Token);
                }
                else
                {
                    appState.StatusMessage = "Invalid latitude or longitude";
                }
            };

            Action cancelSite = () =>
            {
                eqState.IsEditingSite = false;
                eqState.LatitudeInput.Deactivate();
                eqState.LongitudeInput.Deactivate();
                eqState.ElevationInput.Deactivate();
                appState.ActiveTextInput = null;
            };

            eqState.LatitudeInput.OnCommit = _ => saveSite();
            eqState.LongitudeInput.OnCommit = _ => saveSite();
            eqState.ElevationInput.OnCommit = _ => saveSite();
            eqState.LatitudeInput.OnCancel = cancelSite;
            eqState.LongitudeInput.OnCancel = cancelSite;
            eqState.ElevationInput.OnCancel = cancelSite;

            // ---------------------------------------------------------------
            // Equipment action signal subscriptions (DI-dependent handlers)
            // ---------------------------------------------------------------
            var bus = guiRenderer.Bus!;

            bus.Subscribe<DiscoverDevicesSignal>(async _ =>
            {
                if (eqState.IsDiscovering) return;

                eqState.IsDiscovering = true;
                appState.StatusMessage = "Discovering devices...";
                appState.NeedsRedraw = true;
                try
                {
                    var dm = sp.GetRequiredService<ICombinedDeviceManager>();
                    await dm.CheckSupportAsync(cts.Token);
                    await dm.DiscoverAsync(cts.Token);
                    eqState.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                        .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                        .SelectMany(dm.RegisteredDevices)
                        .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Device discovery failed");
                    appState.StatusMessage = "Discovery failed";
                }
                finally
                {
                    eqState.IsDiscovering = false;
                    appState.StatusMessage = null;
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<AddOtaSignal>(async _ =>
            {
                if (appState.ActiveProfile is not { } p) return;

                var data = p.Data ?? ProfileData.Empty;
                var newOta = new OTAData(
                    Name: $"Telescope #{data.OTAs.Length}",
                    FocalLength: 1000,
                    Camera: NoneDevice.Instance.DeviceUri,
                    Cover: null, Focuser: null, FilterWheel: null,
                    PreferOutwardFocus: null, OutwardIsPositive: null,
                    Aperture: null, OpticalDesign: OpticalDesign.Unknown);
                var updated = p.WithData(EquipmentActions.AddOTA(data, newOta));
                appState.ActiveProfile = updated;
                appState.NeedsRedraw = true;
                await updated.SaveAsync(external, cts.Token);
            });

            bus.Subscribe<EditSiteSignal>(_ =>
            {
                eqState.IsEditingSite = true;
                if (appState.ActiveProfile?.Data is { } pd)
                {
                    var existingSite = EquipmentActions.GetSiteFromMount(pd.Mount);
                    if (existingSite.HasValue)
                    {
                        eqState.LatitudeInput.Text = existingSite.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        eqState.LatitudeInput.CursorPos = eqState.LatitudeInput.Text.Length;
                        eqState.LongitudeInput.Text = existingSite.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        eqState.LongitudeInput.CursorPos = eqState.LongitudeInput.Text.Length;
                        eqState.ElevationInput.Text = existingSite.Value.Elev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                        eqState.ElevationInput.CursorPos = eqState.ElevationInput.Text.Length;
                    }
                }
                bus.Post(new ActivateTextInputSignal(eqState.LatitudeInput));
            });

            bus.Subscribe<CreateProfileSignal>(_ =>
            {
                if (!eqState.IsCreatingProfile)
                {
                    eqState.IsCreatingProfile = true;
                    bus.Post(new ActivateTextInputSignal(eqState.ProfileNameInput));
                }
            });

            bus.Subscribe<AssignDeviceSignal>(async sig =>
            {
                var deviceIndex = sig.DeviceIndex;
                if (deviceIndex < 0 || deviceIndex >= eqState.DiscoveredDevices.Count) return;

                if (eqState.ActiveAssignment is { } target && appState.ActiveProfile is { } profile)
                {
                    var device = eqState.DiscoveredDevices[deviceIndex];

                    if (device.DeviceType != target.ExpectedDeviceType)
                    {
                        appState.StatusMessage = $"Expected {target.ExpectedDeviceType}, got {device.DeviceType}";
                        return;
                    }

                    var data = profile.Data ?? ProfileData.Empty;
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
                    appState.ActiveProfile = updated;
                    eqState.ActiveAssignment = null;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);
                }
            });

            bus.Subscribe<UpdateProfileSignal>(async sig =>
            {
                if (appState.ActiveProfile is { } profile)
                {
                    var updated = profile.WithData(sig.Data);
                    appState.ActiveProfile = updated;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);
                }
            });

            // ---------------------------------------------------------------
            // Local helpers captured by closures above
            // ---------------------------------------------------------------
            void CommitSuggestion(string suggestion)
            {
                plannerState.SearchInput.Text = suggestion;
                plannerState.SearchInput.CursorPos = suggestion.Length;
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = suggestion;

                if (appState.ActiveProfile is not null)
                {
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
                    if (transform is not null)
                    {
                        var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        var resultIdx = PlannerActions.CommitSuggestion(plannerState, db, transform, suggestion);
                        if (resultIdx >= 0)
                        {
                            plannerState.SelectedTargetIndex = resultIdx;
                            guiRenderer.PlannerTab.EnsureVisible(resultIdx);
                        }
                    }
                }
                appState.NeedsRedraw = true;
            }

            // Store autocComplete cache setter as a public action
            SetAutoCompleteCache = cache => autoCompleteCache = cache;
        }

        /// <summary>Set by Program.cs after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache { get; }

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
            var hit = _guiRenderer.HitTestAndDispatch(px, py);

            // Auto-discover on tab switch to Equipment
            if (hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("Tab:"))
            {
                if (action == "Tab:Equipment" && _guiRenderer.EquipmentTab.State.DiscoveredDevices.Count == 0)
                {
                    _guiRenderer.Bus?.Post(new DiscoverDevicesSignal());
                }
                _appState.NeedsRedraw = true;
                return true;
            }

            // If chrome didn't handle it, try the active tab
            if (hit is null)
            {
                hit = _guiRenderer.ActiveTab?.HitTestAndDispatch(px, py);
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
        /// Handles mouse wheel scroll — delegates to the active tab.
        /// </summary>
        public void HandleMouseWheel(float scrollY)
        {
            var pos = _appState.MouseScreenPosition;
            if (_guiRenderer.ActiveTab?.HandleMouseWheel(scrollY, pos.X, pos.Y) == true)
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
        }

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
                var inputs = _guiRenderer.ActiveTab?.GetRegisteredTextInputs();
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
                    activeInput.Deactivate();
                    StopTextInput(_sdlWindowHandle);
                }

                _appState.NeedsRedraw = true;
                return true;
            }

            return true; // Swallow all keys when text input active
        }
    }
}
