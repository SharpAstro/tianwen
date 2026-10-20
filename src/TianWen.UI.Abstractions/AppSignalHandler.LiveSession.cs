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
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.Hosting.Dto;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.LiveSession.cs -- live-session + preview-mode signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Wires the live-session signals (session start/stop orchestration).</summary>
        private void SubscribeLiveSession(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            // Capturing the LOCAL session state here is safe -- the local context is created once and
            // never replaced, so the reference stays valid for the process lifetime. A handler that
            // ever routes to the on-screen context must NOT capture like this: it has to resolve
            // _contexts.Active at post time, since the user can switch contexts between subscribe and post.
            var appState = _appState;
            var plannerState = _plannerState;
            var sessionState = _sessionState;
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Live session signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartSessionSignal>(async _ =>
            {
                if (CommandTargetOrSay("A session") is not { } target) return;
                var (node, view) = (target.Node, target.View);

                if (target.Profile is not { } profile)
                {
                    Notify(NotificationSeverity.Warning, "No profile selected");
                    return;
                }

                if (plannerState.Proposals is not { Length: > 0 })
                {
                    Notify(NotificationSeverity.Warning, "No targets \u2014 pin targets in the Planner first");
                    return;
                }

                // The plan is built here, where it is made (SessionStartPlan); the node runs it, so a window that dies or
                // wedges takes none of the night with it. The node refuses a second run, naming the one going on, and its
                // feed notes the run as it goes, in the words a rig's feed uses (SessionNotes).
                if (SessionStartPlan.TryBuild(plannerState, sessionState, profile, _timeProvider, logger, out var problem) is not { } plan)
                {
                    Notify(NotificationSeverity.Error, problem ?? "Failed to build schedule from proposals");
                    return;
                }

                view.ShowAbortConfirm = false;
                appState.ActiveTab = GuiTab.LiveSession;
                appState.StatusMessage = "Starting the session\u2026";
                appState.NeedsRedraw = true;
                var started = await node.Mirror.StartAsync([.. plan.Schedule.Select(ScheduledObservationDto.FromScheduled)],
                    profile.ProfileId, SessionConfigApiDto.FromConfiguration(plan.Configuration), cts.Token);
                appState.StatusMessage = null;
                if (!started.IsSuccess)
                {
                    Notify(NotificationSeverity.Warning, $"The session did not start: {started.Error}");
                }
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<ConfirmAbortSessionSignal>(_ =>
            {
                // The ABORT of the rig on screen, through its own node, this computer's included: the node ends the run
                // through its Finalise (park, warm, close).
                _contexts.Active.LiveSession.ShowAbortConfirm = false;
                StopActiveRun("Aborting the session");
                _contexts.Active.LiveSession.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });
        }

        /// <summary>
        /// How long a still waits for the live view of its camera to end: the node answers a stop once the capture loop has
        /// drained, a frame or two, and a Canon's Live View takes a moment more to close.
        /// </summary>
        private static readonly TimeSpan LiveViewStopBudget = TimeSpan.FromSeconds(10);

        /// <summary>Wires the preview-mode signals (camera preview, live view, snapshot save, plate solve, planetary capture).</summary>
        private void SubscribePreview(SignalBus bus, CancellationToken shutdownToken)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Preview mode signals (camera preview, snapshot save, plate solve)
            // ---------------------------------------------------------------

            // The Preview mode's live view (#1111): the node streams the camera, this shows it and sends its controls.
            var liveView = sp.GetRequiredService<LiveViewController>();
            liveView.RedrawRequested = () => appState.NeedsRedraw = true;

            bus.Subscribe<TakePreviewSignal>(sig =>
            {
                if (CommandTargetOrSay("A preview") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                if (target.Profile?.Data is not { } previewData || sig.OtaIndex >= previewData.OTAs.Length)
                {
                    Notify(NotificationSeverity.Warning, "Invalid OTA index");
                    return;
                }

                // Mark capturing
                if (sig.OtaIndex < view.PreviewCapturing.Length)
                {
                    view.PreviewCapturing[sig.OtaIndex] = true;
                    view.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    view.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.NeedsRedraw = true;

                // The node exposes (it stamps the frame's headers as a session's are), keeps the frame as the OTA's, and
                // pushes FRAME-AVAILABLE, on which the view's mirror fetches it: the frame on show is the node's. The node
                // refuses a camera a run holds, in the run's name.
                var request = new PreviewExposureRequestDto { ExposureSeconds = sig.ExposureSeconds, Gain = sig.Gain is { } g ? (short)g : null, Binning = sig.Binning };
                RunTracked($"PreviewCapture OTA{sig.OtaIndex}", "Preview failed", async ct =>
                {
                    // A camera in a live view takes no still (a Canon's Live View and its shutter exclude each other), so the
                    // live view ends first; the still is what the pane shows once it arrives.
                    if (liveView.LiveOta == sig.OtaIndex)
                    {
                        using var budget = new CancellationTokenSource(LiveViewStopBudget, _timeProvider.System);
                        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
                        await liveView.StopAsync(bounded.Token);
                    }
                    if (await RunNodeJobAsync(node, node.Client.StartPreviewExposureAsync(sig.OtaIndex, request, ct), "Preview", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Preview captured: OTA {sig.OtaIndex + 1}");
                    }
                }, onFinally: () =>
                {
                    if (sig.OtaIndex < view.PreviewCapturing.Length)
                    {
                        view.PreviewCapturing[sig.OtaIndex] = false;
                    }
                    appState.NeedsRedraw = true;
                }, cancelMessage: "Preview cancelled");
            });

            bus.Subscribe<StopPreviewSignal>(_ =>
            {
                if (CommandTargetOrSay("Stopping a preview") is not { } target) return;
                RunTracked("StopPreview", "Could not stop the preview",
                    ct => PictureJobs.StopPreviewsAsync(target.Node.Client, _timeProvider, progress: null, ct));
            });

            bus.Subscribe<StartLiveViewSignal>(sig =>
            {
                if (CommandTargetOrSay("A live view") is not { Node: var node }) return;

                RunTracked($"StartLiveView OTA{sig.OtaIndex}", "Live view failed to start", async ct =>
                {
                    var request = new LiveViewRequestDto
                    {
                        OtaIndex = sig.OtaIndex,
                        ExposureMs = sig.ExposureSeconds * 1000.0,
                        Gain = sig.Gain is { } g ? (short)g : null,
                        Binning = sig.Binning,
                    };
                    // The watching ends with the app; the node's live view ends by itself once nobody watches it.
                    if (await liveView.StartAsync(node, request, shutdownToken) is { } refusal)
                    {
                        Notify(NotificationSeverity.Warning, refusal);
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<StopLiveViewSignal>(_ =>
            {
                liveView.Stop();
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SaveSnapshotSignal>(sig =>
            {
                if (CommandTargetOrSay("A snapshot") is not { Node: var node }) return;

                // Saved by the node, of the frame it shows: its own copy, where a session's subs are written.
                RunTracked("SaveSnapshot", "Snapshot failed", async ct =>
                {
                    var saved = await node.Client.SaveSnapshotAsync(sig.OtaIndex, ct);
                    Notify(saved.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning, saved.IsSuccess
                        ? $"Snapshot saved: {Path.GetFileName(saved.Value)}"
                        : saved.Error ?? "No preview image to save");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<PlateSolvePreviewSignal>(sig =>
            {
                if (CommandTargetOrSay("A plate solve") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                // Drop duplicate clicks: if a solve is already running for this OTA,
                // ignore. The button is rendered as "Solving…" with no click handler,
                // but a stray hit before the redraw could still fire the signal.
                if (sig.OtaIndex < view.PreviewPlateSolving.Length
                    && view.PreviewPlateSolving[sig.OtaIndex])
                {
                    return;
                }

                if (sig.OtaIndex < view.PreviewPlateSolving.Length)
                {
                    view.PreviewPlateSolving[sig.OtaIndex] = true;
                }
                view.NeedsRedraw = true;
                appState.NeedsRedraw = true;

                RunTracked("PreviewPlateSolve", "Plate solve error", async ct =>
                {
                    appState.StatusMessage = "Plate solving\u2026";
                    appState.NeedsRedraw = true;

                    // Solved by the node, of the frame it shows, with its solvers; the solution is the OTA's there.
                    if (await RunNodeJobAsync(node, node.Client.StartSolveAsync(sig.OtaIndex, ct), "Plate solve", ct) is not null)
                    {
                        await ShowSolutionAsync(node, view, sig.OtaIndex, ct);
                    }
                }, onFinally: () =>
                {
                    if (sig.OtaIndex < view.PreviewPlateSolving.Length)
                    {
                        view.PreviewPlateSolving[sig.OtaIndex] = false;
                    }
                    view.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                });
            });

            bus.Subscribe<JogFocuserSignal>(sig =>
            {
                if (!TryResolveOtaFocuser(sig.OtaIndex, out var node, out var focuserUri)) return;

                RunTracked($"JogFocuser OTA{sig.OtaIndex}", "Focuser jog failed", async ct =>
                {
                    var move = new FocuserMoveRequestDto { DeviceUri = focuserUri.ToString(), Steps = sig.Steps };
                    if (await RunNodeJobAsync(node, node.Client.MoveFocuserAsync(move, ct), "Focuser jog", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "Focuser moved");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // A filter change from the planetary panel (#1618): the node's one filter change, which, while its capture streams
            // through the wheel, stops the stream for it and starts it again (#1639), and refuses in words during a recording.
            bus.Subscribe<ChangeFilterSignal>(sig =>
            {
                if (CommandTargetOrSay("A filter change") is not { Node: var node })
                {
                    return;
                }
                RunTracked($"ChangeFilter OTA{sig.OtaIndex}", "Filter change failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.ChangeOtaFilterAsync(sig.OtaIndex, sig.Position, ct), "Filter change", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "Filter changed");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Live planetary capture: the node on show runs it (its camera, its claim, the ROI, the stack, the recenter), this
            // computer's or a rig's this client controls, and the shared PlanetaryCaptureController shows it and sends the
            // panel's controls.
            var planetaryCapture = sp.GetRequiredService<PlanetaryCaptureController>();
            // The status line follows what the node applied (#1619): the start's notice said the start's settings, for ever.
            planetaryCapture.SettingsApplied += words =>
            {
                Notify(NotificationSeverity.Info, $"Planetary capture at {words}");
                appState.NeedsRedraw = true;
            };

            // And it says when a recording has ended, which the start's "Recording 60 s to ..." stayed saying after it.
            planetaryCapture.RecordingEnded += words =>
            {
                Notify(NotificationSeverity.Info, words);
                appState.NeedsRedraw = true;
            };

            bus.Subscribe<StartVideoCaptureSignal>(sig =>
            {
                // The remote mode pill offers Planetary too: it streams the camera of the rig on show, once this client
                // controls it.
                if (CommandTargetOrSay("A planetary capture") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                // The derived sharpening's telescope is the one the view on show is planned with.
                planetaryCapture.SeedTelescope(ProfileOnShow?.Data, sig.OtaIndex);

                RunTracked("StartPlanetaryCapture", "Planetary capture failed to start", async ct =>
                {
                    var request = new PlanetaryRequestDto
                    {
                        OtaIndex = sig.OtaIndex,
                        ExposureMs = sig.ExposureMs,
                        Gain = sig.Gain,
                        RoiWidth = sig.RoiWidth,
                        RoiHeight = sig.RoiHeight,
                        BitDepth = sig.BitDepth,
                        HighSpeed = sig.HighSpeed,
                        Recenter = planetaryCapture.Capture.RecenterForStart,
                    };
                    // The watching ends with the app; the node's capture goes on until it is stopped, as a node's runs do.
                    if (await planetaryCapture.StartAsync(node, request, shutdownToken) is { } refusal)
                    {
                        Notify(NotificationSeverity.Warning, refusal);
                        return;
                    }

                    // Planetary capture is now a Live Session mode (not a standalone tab): show it there.
                    view.Mode = LiveSessionMode.Planetary;
                    appState.ActiveTab = GuiTab.LiveSession;
                    var (roiWidth, roiHeight) = planetaryCapture.Capture.Roi;
                    Notify(NotificationSeverity.Info, $"Planetary capture started ({roiWidth}x{roiHeight}, {sig.ExposureMs:F0} ms)");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Reframing on the full sensor (#1033): the node takes the still as a job, the view shows it with the live window and a
            // proposal; a commit is the node's reframe job, a cancel only leaves the view.
            bus.Subscribe<TakeFullFrameSignal>(_ =>
            {
                if (!planetaryCapture.IsCapturing || planetaryCapture.Node is not { } node)
                {
                    return;
                }
                RunTracked("Planetary full frame", "Full frame failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.TakePlanetaryFullFrameAsync(ct), "Full frame", ct) is null)
                    {
                        return;
                    }
                    var frame = await node.Client.GetLatestFrameAsync(FrameSources.PlanetaryFullFrame, after: null, new FrameReader(), ct);
                    if (frame.Image is not { } still)
                    {
                        Notify(NotificationSeverity.Warning, $"The full frame could not be read: {frame.Error}");
                        return;
                    }
                    var (width, height) = planetaryCapture.Capture.Roi;
                    var live = planetaryCapture.Capture.Window is { } at ? at with { Width = width, Height = height }
                        : new RoiRect((still.Width - width) / 2, (still.Height - height) / 2, width, height);
                    planetaryCapture.BeginReframe(still, live, RoiConstraints.ForSensor(still.Width, still.Height));
                    Notify(NotificationSeverity.Info, "Drag the window on the full frame, then move it there or cancel");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<CommitReframeSignal>(_ =>
            {
                if (planetaryCapture.Reframe is not { } reframe || planetaryCapture.Node is not { } node)
                {
                    return;
                }
                var target = reframe.Proposal;
                planetaryCapture.EndReframe();
                if (!reframe.Moves)
                {
                    return;
                }
                RunTracked("Planetary reframe", "Reframe failed", async ct =>
                {
                    var request = new PlanetaryReframeRequestDto { X = target.X, Y = target.Y, Width = target.Width, Height = target.Height };
                    if (await RunNodeJobAsync(node, node.Client.ReframePlanetaryAsync(request, ct), "Reframe", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "The window moved");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Finding the planet (#1684): the node takes a full-sensor still and solves it as a job, and its result is the capture
            // state's, which the FIND section shows; the sync and the goto are a second job, asked for on the result.
            bus.Subscribe<FindPlanetSignal>(sig =>
            {
                if (!planetaryCapture.IsCapturing || planetaryCapture.Node is not { } node)
                {
                    return;
                }
                RunTracked("Planetary find", "The find failed", async ct =>
                {
                    var request = new PlanetaryFindRequestDto { ExposureSeconds = sig.ExposureSeconds, Planet = sig.Planet };
                    if (await RunNodeJobAsync(node, node.Client.FindPlanetAsync(request, ct), "Find", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "Solved");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<FindPlanetGoSignal>(sig =>
            {
                if (planetaryCapture.Node is not { } node)
                {
                    return;
                }
                RunTracked("Planetary find go", "The sync failed", async ct =>
                {
                    var request = new PlanetaryFindGoRequestDto { Planet = sig.Planet, SyncOnly = sig.SyncOnly };
                    if (await RunNodeJobAsync(node, node.Client.FindPlanetGoAsync(request, ct), sig.SyncOnly ? "Sync" : "Go to the planet", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "Synced");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // The operator took the focal length a find measured: written into the profile as the Equipment tab's edit writes it.
            bus.Subscribe<UseMeasuredFocalLengthSignal>(sig =>
            {
                if (appState.ActiveProfile is { Data: { } data })
                {
                    bus.Post(new UpdateProfileSignal(EquipmentActions.UpdateOTA(data, sig.OtaIndex, focalLength: sig.FocalLengthMm)));
                    Notify(NotificationSeverity.Info, $"The profile's focal length is now {sig.FocalLengthMm} mm");
                }
            });

            // A sequence on the capture on show (#1340): the node runs it and its state carries the progress the panel shows.
            bus.Subscribe<StartPlanetarySequenceSignal>(sig =>
            {
                if (!planetaryCapture.IsCapturing || planetaryCapture.Node is not { } node)
                {
                    Notify(NotificationSeverity.Warning, "A sequence runs on a planetary capture: start the capture first");
                    return;
                }
                RunTracked("Planetary sequence", "The sequence could not start", async ct =>
                {
                    var started = await node.Client.StartPlanetarySequenceAsync(sig.Request, ct);
                    if (!started.IsSuccess)
                    {
                        Notify(NotificationSeverity.Warning, started.Error ?? "The node did not start the sequence");
                        return;
                    }
                    Notify(NotificationSeverity.Info, $"Sequence started: {sig.Request.Sets} set{(sig.Request.Sets == 1 ? "" : "s")} of {sig.Request.Steps.Length} steps");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<StopPlanetarySequenceSignal>(_ =>
            {
                if (planetaryCapture.Node is not { } node)
                {
                    return;
                }
                RunTracked("Planetary sequence stop", "The sequence could not be stopped", async ct =>
                {
                    var stopped = await node.Client.StopPlanetarySequenceAsync(ct);
                    Notify(stopped.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning,
                        stopped.IsSuccess ? "Sequence stopped; the step's frames are kept" : stopped.Error ?? "The node did not stop the sequence");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<CancelReframeSignal>(_ =>
            {
                planetaryCapture.EndReframe();
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<StopVideoCaptureSignal>(_ =>
            {
                if (!planetaryCapture.IsCapturing) return;
                planetaryCapture.Stop();
                Notify(NotificationSeverity.Info, "Planetary capture stopped");
            });

            // The Record control (#1344): the capture's own node records it to a SER (its one writer) and finishes it unwatched;
            // the panel reads it back from the capture's state. A control of the run on screen goes to its own node, so neither
            // signal resolves the view's command target (CLAUDE.md, Remote Rigs).
            bus.Subscribe<StartPlanetaryRecordingSignal>(sig =>
            {
                RunTracked("StartPlanetaryRecording", "Recording failed to start", async ct =>
                {
                    var answer = await planetaryCapture.RecordAsync(sig.Duration, ct);
                    Notify(answer.Refusal is null ? NotificationSeverity.Info : NotificationSeverity.Warning,
                        answer.Refusal ?? $"Recording {sig.Duration.TotalSeconds:0.#} s to {Path.GetFileName(answer.Recording?.Path)}");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<StopPlanetaryRecordingSignal>(_ =>
            {
                RunTracked("StopPlanetaryRecording", "Recording failed to stop", async ct =>
                {
                    var answer = await planetaryCapture.StopRecordingAsync(ct);
                    Notify(answer.Refusal is null ? NotificationSeverity.Info : NotificationSeverity.Warning, answer.Refusal ?? "Recording stopped");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Manual mount nudge (planetary panel coarse-recenter buttons): one guide-rate pulse, as the node's job, which
            // ends when the mount reports the pulse done and is refused on a mount a run holds, in the run's name.
            bus.Subscribe<JogMountSignal>(sig =>
            {
                if (CommandTargetOrSay("A mount nudge") is not { Node: var node, Profile.Data: { } pdata }) return;
                if (pdata.Mount is not { Scheme: not "none" } mountUri) return;

                RunTracked($"JogMount {sig.Direction}", "Mount jog failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.NudgeMountAsync(mountUri, sig.Direction, sig.Arcsec, ct), "Mount nudge", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Mount nudge {sig.Direction} {sig.Arcsec:F0} arcsec");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<GotoFocuserSignal>(sig =>
            {
                if (!TryResolveOtaFocuser(sig.OtaIndex, out var node, out var focuserUri)) return;

                RunTracked($"GotoFocuser OTA{sig.OtaIndex}", "Focuser goto failed", async ct =>
                {
                    var move = new FocuserMoveRequestDto { DeviceUri = focuserUri.ToString(), Position = sig.TargetPosition };
                    if (await RunNodeJobAsync(node, node.Client.MoveFocuserAsync(move, ct), "Focuser goto", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Focuser \u2192 {sig.TargetPosition}");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });
        }
    }
}
