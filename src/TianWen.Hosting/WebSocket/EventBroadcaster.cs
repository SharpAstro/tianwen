using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.WebSocket;

/// <summary>
/// Background service that subscribes to <see cref="ISession"/> events and broadcasts them
/// to all connected WebSocket clients via <see cref="EventHub"/>.
/// </summary>
internal sealed class EventBroadcaster(
    IHostedSession hostedSession,
    EventHub eventHub,
    ILogger<EventBroadcaster> logger
) : BackgroundService
{
    private ISession? _subscribedSession;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EventBroadcaster started, waiting for session");

        while (!stoppingToken.IsCancellationRequested)
        {
            var session = hostedSession.CurrentSession;

            // Subscribe to new session events
            if (session is not null && !ReferenceEquals(session, _subscribedSession))
            {
                if (_subscribedSession is not null)
                {
                    UnsubscribeFromSession(_subscribedSession);
                }
                SubscribeToSession(session);
                _subscribedSession = session;
                logger.LogInformation("EventBroadcaster subscribed to session");
            }
            else if (session is null && _subscribedSession is not null)
            {
                UnsubscribeFromSession(_subscribedSession);
                _subscribedSession = null;
            }

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_subscribedSession is not null)
        {
            UnsubscribeFromSession(_subscribedSession);
        }
    }

    private void SubscribeToSession(ISession session)
    {
        session.PhaseChanged += OnPhaseChanged;
        session.FrameWritten += OnFrameWritten;
        session.PlateSolveCompleted += OnPlateSolveCompleted;
        session.ScoutCompleted += OnScoutCompleted;
    }

    private void UnsubscribeFromSession(ISession session)
    {
        session.PhaseChanged -= OnPhaseChanged;
        session.FrameWritten -= OnFrameWritten;
        session.PlateSolveCompleted -= OnPlateSolveCompleted;
        session.ScoutCompleted -= OnScoutCompleted;
    }

    private void OnPhaseChanged(object? sender, SessionPhaseChangedEventArgs e)
    {
        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "SESSION-PHASE-CHANGED",
            Data = new Dictionary<string, object?>
            {
                ["OldPhase"] = e.OldPhase.ToString(),
                ["NewPhase"] = e.NewPhase.ToString()
            }
        });
    }

    private void OnFrameWritten(object? sender, FrameWrittenEventArgs e)
    {
        var entry = e.Entry;
        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "FRAME-WRITTEN",
            Data = new Dictionary<string, object?>
            {
                ["TargetName"] = entry.TargetName,
                ["FilterName"] = entry.FilterName,
                ["ExposureSeconds"] = entry.Exposure.TotalSeconds,
                ["FrameNumber"] = entry.FrameNumber,
                ["MedianHfd"] = entry.MedianHfd,
                ["StarCount"] = entry.StarCount
            }
        });
    }

    private void OnPlateSolveCompleted(object? sender, PlateSolveCompletedEventArgs e)
    {
        var record = e.Record;
        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "PLATE-SOLVE-COMPLETED",
            Data = new Dictionary<string, object?>
            {
                ["Context"] = record.Context.ToString(),
                ["OtaName"] = record.OtaName,
                ["Succeeded"] = record.Succeeded,
                ["SolvedRA"] = record.Solution?.CenterRA,
                ["SolvedDec"] = record.Solution?.CenterDec,
                ["ElapsedMs"] = record.Elapsed.TotalMilliseconds,
                ["DetectedStars"] = record.DetectedStars,
                ["MatchedStars"] = record.MatchedStars
            }
        });
    }

    private void OnScoutCompleted(object? sender, ScoutCompletedEventArgs e)
    {
        _ = BroadcastSafeAsync(new WebSocketEventDto
        {
            Event = "SCOUT-COMPLETED",
            Data = new Dictionary<string, object?>
            {
                ["TargetName"] = e.Target.Name,
                ["Classification"] = e.Classification.ToString(),
                ["Outcome"] = e.Outcome.ToString(),
                ["EstimatedClearInSeconds"] = e.EstimatedClearIn?.TotalSeconds,
                ["StarCountsPerOTA"] = e.StarCountsPerOTA
            }
        });
    }

    private async Task BroadcastSafeAsync(WebSocketEventDto eventDto)
    {
        try
        {
            if (eventHub.ClientCount > 0)
            {
                await eventHub.BroadcastAsync(eventDto);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast event {Event}", eventDto.Event);
        }
    }
}
