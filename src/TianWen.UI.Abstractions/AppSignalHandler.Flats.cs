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
    // AppSignalHandler.Flats.cs -- Flats-mode signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Wires the Flats-mode signals (on-demand flat capture -- LiveSessionMode.Flats).</summary>
        private void SubscribeFlats(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var sessionState = _sessionState;
            var liveSessionState = _liveSessionState;
            var tracker = _tracker;
            var cts = _cts;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Flats signals (on-demand flat capture -- LiveSessionMode.Flats)
            // ---------------------------------------------------------------

            bus.Subscribe<StartFlatsSignal>(async sig =>
            {
                if (!EnsureSessionIdle("Session is running \u2014 flats unavailable")) return;
                if (liveSessionState.FlatsCts is not null)
                {
                    Notify(NotificationSeverity.Warning, "Flat run already running");
                    return;
                }
                if (appState.ActiveProfile is not { Data: { } profileData } profile || profileData.OTAs.Length == 0)
                {
                    Notify(NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }

                // Site drives the mount sync + denorm stamp + sky-flat solar-altitude gate; NaN falls back
                // to the mount's own site inside ConnectForFlatsAsync (matches the CLI path).
                var siteLat = profileData.SiteLatitude ?? double.NaN;
                var siteLon = profileData.SiteLongitude ?? double.NaN;

                // Everything past the preconditions -- factory init, config injection, session create,
                // event wiring, tracked RunFlatsOnlyAsync -- lives in FlatsBootstrapper so this lambda
                // routes only (see CLAUDE.md "Signal Handler Pattern").
                await FlatsBootstrapper.BuildAndStartAsync(
                    sp.GetRequiredService<ISessionFactory>(),
                    appState, sessionState, liveSessionState, profile,
                    sig.Source, sig.FlatsPerFilter, siteLat, siteLon,
                    tracker, _timeProvider, logger, cts.Token);
            });

            bus.Subscribe<CancelFlatsSignal>(_ =>
            {
                // The finaliser (close covers, warm, disconnect) still runs on cancel via RunFlatsOnlyAsync's
                // finally block; the panel's Cancel button shows the amber "Cancelling..." state meanwhile.
                liveSessionState.FlatsCts?.Cancel();
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<RespondSessionPromptSignal>(sig =>
            {
                // Forward the user's Continue/Cancel to the session's awaiting prompt and drop the overlay.
                if (liveSessionState.PendingPrompt is { } prompt)
                {
                    prompt.Respond(sig.Proceed);
                    liveSessionState.PendingPrompt = null;
                    liveSessionState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                }
            });
        }
    }
}
