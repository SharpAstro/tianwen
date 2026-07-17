using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.Planner.cs -- planner search input callbacks + schedule building.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Autocomplete candidates for the planner search box; set by the host
        /// after catalog load via <see cref="SetAutoCompleteCache"/>. Was a ctor local
        /// shared with the search closures before the by-area split.</summary>
        private string[]? _autoCompleteCache;

        /// <summary>Wires the planner search input callbacks (autocomplete, suggestion commit).
        /// The callback bodies live in the host-agnostic <see cref="PlannerSearchInteraction"/>
        /// (shared with the web host); this method supplies the desktop-flavoured context
        /// (profile-derived transform, signal-based focus release).</summary>
        private void SubscribePlannerSearch(SignalBus bus)
        {
            var appState = _appState;
            var db = _sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();

            PlannerSearchInteraction.Wire(
                _plannerState, db,
                createTransform: () => appState.ActiveProfile is { } profile
                    ? TransformFactory.FromProfile(profile, _timeProvider, out _)
                    : null,
                autoComplete: () => _autoCompleteCache,
                ensureVisible: idx => OnPlannerEnsureVisible?.Invoke(idx),
                deactivate: () => bus.Post(new DeactivateTextInputSignal()),
                requestRedraw: () => appState.NeedsRedraw = true);
        }

        /// <summary>Wires schedule building (shared between planner preview and session start).</summary>
        private void SubscribeScheduleBuilding(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var plannerState = _plannerState;
            var sessionState = _sessionState;

            // ---------------------------------------------------------------
            // Schedule building (shared between planner preview and session start)
            // ---------------------------------------------------------------
            bus.Subscribe<BuildScheduleSignal>(signal =>
            {
                if (appState.ActiveProfile is not { } profile) return;
                var transform = TransformFactory.FromProfile(profile, _timeProvider, out _);
                if (transform is null) return;

                var profileData = profile.Data ?? ProfileData.Empty;
                var (filters, design) = GetFirstOtaFilterConfig(profileData);
                // Use the SAME effective defaults the session-start path uses so the planner
                // PREVIEW matches what the session will actually capture (it previously hardcoded
                // gain=120/offset=10/sub=120s here, diverging from both the config and the
                // f-ratio exposure shown on the target rows). Gain/offset stay null so the per-OTA
                // camera settings drive them, exactly as StartSession does.
                PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                    defaultGain: null, defaultOffset: null,
                    defaultSubExposure: SessionContent.EffectiveDefaultSubExposure(sessionState),
                    defaultObservationTime: TimeSpan.FromMinutes(60),
                    availableFilters: filters,
                    opticalDesign: design);
            });
        }
    }
}
