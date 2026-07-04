using Shouldly;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// Live smoke test of the extracted <see cref="JsonRpcClient"/> against a real running PHD2
/// (the JSON-RPC server on 127.0.0.1:4400 for instance 1). Exercises exactly the path the refactored
/// <c>OpenPHD2GuiderDriver</c> now relies on -- TCP connect, the receive loop, and <c>CallAsync</c>
/// id-correlation -- so it proves the generalization didn't break real PHD2 interop. Auto-skips when
/// PHD2 isn't listening, so a bare <c>dotnet test</c> stays green.
/// </summary>
public class Phd2SmokeTests
{
    private const int Phd2Port = 4400; // instance 1

    private static bool Phd2Reachable()
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(IPAddress.Loopback, Phd2Port).Wait(TimeSpan.FromMilliseconds(500)) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task GivenARunningPhd2WhenCallingGetAppStateThenACorrelatedResultIsReturned()
    {
        Assert.SkipUnless(Phd2Reachable(), $"Skipped unless PHD2 is running (JSON-RPC on 127.0.0.1:{Phd2Port}).");

        var ct = TestContext.Current.CancellationToken;

        var connection = new JsonRpcOverTcpConnection();
        await connection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Phd2Port), ct);

        using var client = new JsonRpcClient(connection);
        var loop = client.ReceiveLoopAsync(ct);

        // get_app_state is read-only and valid even with no equipment connected; it returns a string
        // like "Stopped" / "Looping" / "Guiding".
        using (var response = await client.CallAsync("get_app_state", writeParams: null, ct))
        {
            var appState = response.RootElement.GetProperty("result").GetString();
            appState.ShouldNotBeNullOrEmpty();
        }

        // A second call proves id-correlation advances (id 2 matched, not the cached id-1 response).
        using (var version = await client.CallAsync("get_app_state", writeParams: null, ct))
        {
            version.RootElement.TryGetProperty("result", out _).ShouldBeTrue();
        }

        connection.Dispose();
    }

    [Fact]
    public async Task GivenARunningPhd2WhenConnectedThenAServerNotificationIsRoutedToTheHandler()
    {
        Assert.SkipUnless(Phd2Reachable(), $"Skipped unless PHD2 is running (JSON-RPC on 127.0.0.1:{Phd2Port}).");

        var ct = TestContext.Current.CancellationToken;
        var firstEvent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = new JsonRpcOverTcpConnection();
        await connection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Phd2Port), ct);

        // PHD2 pushes a "Version" event (a notification: has "Event", no "id") to every client right
        // after it connects -- exercising exactly the onNotification path the refactor added (+ the
        // doc-disposal wrapper the driver uses).
        using var client = new JsonRpcClient(connection, onNotification: (doc, _) =>
        {
            if (doc.RootElement.TryGetProperty("Event", out var ev))
            {
                firstEvent.TrySetResult(ev.GetString() ?? "");
            }
            doc.Dispose();
            return ValueTask.CompletedTask;
        });
        _ = client.ReceiveLoopAsync(ct);

        var eventName = await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        eventName.ShouldNotBeNullOrEmpty(); // typically "Version"

        connection.Dispose();
    }
}
