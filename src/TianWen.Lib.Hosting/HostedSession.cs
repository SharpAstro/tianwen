using Microsoft.Extensions.Hosting;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting;

public interface IHostedSession : IHostedService
{
    ISession? CurrentSession { get; }
}

internal class HostedSession(ISessionFactory sessionFactory) : IHostedSession
{
    private ISession? _session;
    private CancellationTokenSource? _cts;

    public ISession? CurrentSession => Interlocked.CompareExchange(ref _session, null, null);

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
            // TODO: there's more to do here to gracefully stop a session
            await session.DisposeAsync();
        }
    }
}
