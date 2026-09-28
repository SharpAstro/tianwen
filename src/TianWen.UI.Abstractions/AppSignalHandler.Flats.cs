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
using TianWen.Hosting.Dto;

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
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var cts = _cts;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Flats signals (on-demand flat capture -- LiveSessionMode.Flats)
            // ---------------------------------------------------------------

            bus.Subscribe<StartFlatsSignal>(async sig =>
            {
                if (CommandTargetOrSay("A flat run") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                if (target.Profile is not { Data: { } profileData } profile || profileData.OTAs.Length == 0)
                {
                    Notify(NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }

                // Run by the node: connect, cool, capture, finalise, and its prompts (switch the panel on) on this view
                // through the connection's one prompt wiring. It starts from the session tab's configuration, so the flats
                // cool to the setpoint the lights are taken at.
                var (source, period) = sig.Source switch
                {
                    FlatIlluminationChoice.SkyDusk => ("sky", "dusk"),
                    FlatIlluminationChoice.SkyDawn => ("sky", "dawn"),
                    _ => ("calibrator", (string?)null),
                };
                var request = new FlatsRequestDto
                {
                    Source = source,
                    Period = period,
                    Count = sig.FlatsPerFilter,
                    Configuration = SessionConfigApiDto.FromConfiguration(SessionStartPlan.ForFlats(sessionState, profileData)),
                };

                view.Mode = LiveSessionMode.Flats;
                view.FlatStatusMessage = "Starting flat run\u2026";
                view.FlatCancelRequested = false;
                view.NeedsRedraw = true;
                appState.ActiveTab = GuiTab.LiveSession;
                appState.NeedsRedraw = true;

                var started = await node.Mirror.StartFlatsAsync(request, profile.ProfileId, cts.Token);
                if (!started.IsSuccess)
                {
                    view.FlatStatusMessage = started.Error;
                    Notify(NotificationSeverity.Warning, $"The flat run did not start: {started.Error}");
                }
                view.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<CancelFlatsSignal>(_ =>
            {
                // The flat run of the rig on screen, through its own node, this computer's included. The node's finaliser
                // (close covers, warm, disconnect) still runs; the panel shows "Cancelling..." until the run has ended.
                _contexts.Active.LiveSession.FlatCancelRequested = true;
                StopActiveRun("Cancelling the flat run");
                _contexts.Active.LiveSession.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<RespondSessionPromptSignal>(sig =>
            {
                // The answer goes to the prompt on screen: this computer's run's, or a rig's, which its mirror sends to the
                // rig's node (P5b part 5). It used to answer this computer's prompt whatever view it was given on.
                var view = _contexts.Active.LiveSession;
                if (view.PendingPrompt is { } prompt)
                {
                    prompt.Respond(sig.Proceed);
                    view.PendingPrompt = null;
                    view.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                }
            });
        }
    }
}
