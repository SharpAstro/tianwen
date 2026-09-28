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
using TianWen.Lib;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.Polar.cs -- polar-alignment signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Wires the polar-alignment signals (start/cancel/done + refine-loop state).</summary>
        private void SubscribePolarAlignment(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Polar alignment signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartPolarAlignmentSignal>(sig =>
            {
                if (CommandTargetOrSay("Polar alignment") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                if (view.PolarAlignmentCts is not null)
                {
                    Notify(NotificationSeverity.Warning, "Polar alignment already running");
                    return;
                }
                if (target.Profile?.Data is null)
                {
                    Notify(NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }

                // The setup panel supplies the full configuration; the toolbar and the TUI pin only the rotation. The run is
                // the node's (PolarAlignmentRun, its claim on the mount and the camera, its restore however it ends); this
                // view watches its state and asks it to stop, and its frames are the OTA's, which the view's mirror fetches.
                var configuration = sig.Configuration ?? (PolarAlignmentConfiguration.Default with { RotationDeg = sig.DeltaRaDeg });
                var request = new PolarAlignmentRequestDto
                {
                    OtaIndex = sig.OtaIndex,
                    UseGuider = sig.UseGuider,
                    Configuration = PolarAlignmentConfigDto.From(configuration),
                };

                // The view's handle on the run: cancelling it asks the node to stop, which restores the mount.
                var polarCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                view.PolarAlignmentCts = polarCts;
                view.Mode = LiveSessionMode.PolarAlign;
                view.PolarStatusMessage = "Starting polar alignment\u2026";
                appState.NeedsRedraw = true;

                // Completed once the node's run has ended, its restore included, whatever it came to.
                var ended = view.BeginPolarRun();
                tracker.Run(async () =>
                {
                    try
                    {
                        await WatchPolarRunAsync(node, view, request, polarCts, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // The app is going: the node's run goes on, as a node's runs do, and ends on its own.
                    }
                    catch (Exception ex)
                    {
                        Notify(NotificationSeverity.Error, $"Polar alignment failed: {ex.Message}");
                    }
                    finally
                    {
                        view.PolarAlignmentCts = null;
                        polarCts.Dispose();
                        // Drop back into preview mode unless the user already swapped tabs.
                        if (view.Mode == LiveSessionMode.PolarAlign)
                        {
                            view.Mode = LiveSessionMode.Preview;
                        }
                        view.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                        ended.TrySetResult();
                    }
                }, "PolarAlignment");
            });

            bus.Subscribe<CancelPolarAlignmentSignal>(_ =>
            {
                // The Cancel button itself flips to an amber "Cancelling..." state
                // while PolarAlignmentCts.IsCancellationRequested is true, and the
                // phase pill carries the technical "RESTORING" badge as the mount
                // reverses, so a third copy on the status line would be redundant. The run this view watches: this
                // computer's, or a rig's this client started.
                var view = _contexts.Active.LiveSession;
                view.PolarAlignmentCts?.Cancel();
                view.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<NudgeFakeMountMisalignmentSignal>(_ =>
            {
                // The keys can't turn physical knobs: say so rather than swallow the input, or pressing arrows on a real
                // mount looks broken. A simulated rig's misalignment is its fake mount's own query key, set in the profile.
                if (appState.ActiveProfile?.Data?.Mount is null)
                {
                    return;
                }
                liveSessionState.PolarStatusMessage = "Adjust the mount's alt/az knobs to refine";
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<DonePolarAlignmentSignal>(_ =>
            {
                // Done is the same exit path as Cancel: the node stops the refine loop and applies the configured OnDone
                // behaviour (ReverseAxisBack by default).
                var view = _contexts.Active.LiveSession;
                view.PolarAlignmentCts?.Cancel();
                view.PolarStatusMessage = "Restoring mount\u2026";
                view.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });
        }

        /// <summary>How often a polar run's state is read from the node while it goes on: its refine loop ticks at a few Hz.</summary>
        private static readonly TimeSpan PolarStatePollInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Starts the node's polar run and shows its state until it has ended (<c>GET /api/v1/polar</c>, lossless): its phase
        /// and status, Phase A's result, the refinement ticks and the latest solve. Asks the node to stop, once, when
        /// <paramref name="stop"/> is cancelled, and goes on reading until the node says the run is over, its restore done.
        /// </summary>
        private async Task WatchPolarRunAsync(NodeConnection node, LiveSessionState view, PolarAlignmentRequestDto request,
            CancellationTokenSource stop, CancellationToken appToken)
        {
            var started = await node.Client.StartPolarAlignmentAsync(request, appToken).ConfigureAwait(false);
            if (started is not { IsSuccess: true, Value: { } state })
            {
                Notify(NotificationSeverity.Warning, started.Error ?? "Polar alignment did not start");
                return;
            }
            ShowPolarState(view, state);

            var stopAsked = false;
            while (true)
            {
                if (stop.IsCancellationRequested && !stopAsked)
                {
                    stopAsked = true;
                    var stopped = await node.Client.StopPolarAlignmentAsync(appToken).ConfigureAwait(false);
                    if (!stopped.IsSuccess && !stopped.IsNotFound)
                    {
                        Notify(NotificationSeverity.Warning, $"Polar alignment did not stop: {stopped.Error}");
                    }
                }

                try
                {
                    await _timeProvider.SleepAsync(PolarStatePollInterval, stopAsked ? appToken : stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
                {
                    // Cancel was pressed: ask the node to stop at once rather than after the sleep.
                }

                var now = await node.Client.GetPolarAlignmentAsync(appToken).ConfigureAwait(false);
                if (now is { IsSuccess: true, Value: { } current })
                {
                    ShowPolarState(view, current);
                    if (!current.Running)
                    {
                        if (current.FailureReason is { } failure)
                        {
                            Notify(NotificationSeverity.Warning, $"Polar alignment: {failure}");
                        }
                        return;
                    }
                }
                else if (now.IsNotFound)
                {
                    // Another run has replaced it on the node: nothing of this one is left to show.
                    return;
                }
            }
        }

        /// <summary>
        /// Reflects a polar run's state, as its node reports it, into the live view, which the render thread reads each frame.
        /// The frame it solved is the OTA's, shown by the view's mirror; its solution is the preview's.
        /// </summary>
        private static void ShowPolarState(LiveSessionState live, PolarStateDto state)
        {
            live.PolarPhase = state.Phase;
            live.PolarStatusMessage = state.StatusMessage;
            live.PolarPhaseAResult = state.PhaseA?.ToResult();
            live.LastPolarSolve = state.LastSolve?.ToResult();
            if (state.Wcs is { } wcs)
            {
                live.PreviewPlateSolveResult = new PlateSolveResult(wcs.ToWcs(), TimeSpan.Zero);
            }
            live.NeedsRedraw = true;
        }
    }
}
