using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting;

public interface IHostedSession : IHostedService
{
    ISession? CurrentSession { get; }

    /// <summary>Active profile ID, set before starting a session or via profile/switch.</summary>
    Guid? ActiveProfileId { get; }

    /// <summary>Targets queued before session start. Drained into the session when it begins.</summary>
    IReadOnlyList<PendingTarget> PendingTargets { get; }

    void SetActiveProfile(Guid profileId);
    void AddTarget(PendingTarget target);
    void ClearTargets();
}

internal class HostedSession(ISessionFactory sessionFactory) : IHostedSession
{
    private ISession? _session;
    private CancellationTokenSource? _cts;
    private Guid? _activeProfileId;
    private readonly List<PendingTarget> _pendingTargets = [];
    private readonly object _targetLock = new object();

    public ISession? CurrentSession => Interlocked.CompareExchange(ref _session, null, null);

    public Guid? ActiveProfileId => _activeProfileId;

    public IReadOnlyList<PendingTarget> PendingTargets
    {
        get
        {
            lock (_targetLock)
            {
                return [.. _pendingTargets];
            }
        }
    }

    public void SetActiveProfile(Guid profileId)
    {
        _activeProfileId = profileId;
    }

    public void AddTarget(PendingTarget target)
    {
        lock (_targetLock)
        {
            _pendingTargets.Add(target);
        }
    }

    public void ClearTargets()
    {
        lock (_targetLock)
        {
            _pendingTargets.Clear();
        }
    }

    /// <summary>
    /// Drains pending targets and clears the list. Called by session start endpoints.
    /// </summary>
    internal PendingTarget[] DrainTargets()
    {
        lock (_targetLock)
        {
            var result = _pendingTargets.ToArray();
            _pendingTargets.Clear();
            return result;
        }
    }

    /// <summary>
    /// Sets the active session (called when a session is created via the API or signal bus).
    /// </summary>
    internal void SetSession(ISession session)
    {
        Interlocked.Exchange(ref _session, session);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var oldCts = Interlocked.Exchange(ref _cts, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (oldCts is { IsCancellationRequested: false })
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }
        await sessionFactory.InitializeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _cts, null) is { IsCancellationRequested: false } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            // TODO: graceful shutdown — park mount, warm cameras, close covers
            await session.DisposeAsync();
        }
    }
}
