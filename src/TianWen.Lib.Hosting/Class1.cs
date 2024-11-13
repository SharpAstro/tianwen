using Microsoft.Extensions.Hosting;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

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
        await sessionFactory.InitializeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is { } cts && Interlocked.Exchange(ref _session, null) is { })
        {
            await cts.CancelAsync();
        }
    }
}
