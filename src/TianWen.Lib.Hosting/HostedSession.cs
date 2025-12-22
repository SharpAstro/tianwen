using Microsoft.Extensions.Hosting;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting;

public interface IHostedSession : IHostedService
{
    bool IsRunning { get; }
}

internal class HostedSession(ISessionFactory sessionFactory) : IHostedSession
{
    private Session? _session;
    private CancellationTokenSource? _cts;

    public bool IsRunning => Interlocked.CompareExchange(ref _session, null, null) != null;

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

        if (Interlocked.Exchange(ref _session, null) is { })
        {
            // TODO ensure session is finalized
        }
    }
}
