using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A client window on a test's node: attached, and beating from its loop while it draws, as the GUI beats from the loop
/// that draws it. <see cref="Drawing"/> false is a window that froze, its socket still open. What the detach grace of an
/// interactive run (polar alignment, a planetary live view) is measured against.
/// </summary>
internal sealed class NodeWindow : IAsyncDisposable
{
    private readonly TianWenEventStream _stream;
    private readonly CancellationTokenSource _closed = new CancellationTokenSource();
    private readonly Task _loop;
    private volatile bool _drawing = true;

    private NodeWindow(TianWenEventStream stream)
    {
        _stream = stream;
        _loop = Task.Run(async () =>
        {
            while (!_closed.IsCancellationRequested)
            {
                if (_drawing)
                {
                    _stream.Beat();
                }
                await Task.Delay(16, CancellationToken.None);
            }
        }, CancellationToken.None);
    }

    public bool Drawing { set => _drawing = value; }

    /// <summary>What the window hears from the node.</summary>
    public TianWenEventStream Events => _stream;

    /// <summary>Opens a window on <paramref name="node"/> and waits until the node counts it as present.</summary>
    public static async Task<NodeWindow> OpenAsync(NodeHarness node, ITestOutputHelper outputHelper, CancellationToken ct)
    {
        var stream = node.Transport.CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.Start(ct);
        var window = new NodeWindow(stream);
        var clients = node.App.Services.GetRequiredService<EventHub>();
        await UntilAsync<string>("the window to be present", _ => ValueTask.FromResult<(string?, string)>(
            (clients.PresentClientCount > 0 ? "present" : null, $"{clients.PresentClientCount} present")), ct);
        return window;
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();
        await _loop;
        await _stream.DisposeAsync();
        _closed.Dispose();
    }
}
