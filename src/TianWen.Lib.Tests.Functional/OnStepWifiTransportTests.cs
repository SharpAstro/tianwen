using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OnStep;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Verifies the OnStep WiFi/Ethernet TCP transport (TcpSerialConnection) end-to-end:
/// real socket, real driver code, fake OnStep responder running in-process.
/// </summary>
public class OnStepWifiTransportTests(ITestOutputHelper outputHelper)
{
    [Fact(Timeout = 30_000)]
    public async Task GivenOnStepWifiResponderWhenConnectingDriverIssuesGvpAndGvn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var responder = await FakeOnStepWifiResponder.StartAsync(ct);

        var device = new OnStepDevice(DeviceType.Mount, "test-id", "OnStep WiFi Test", responder.Host, responder.Port);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new OnStepMountDriver<OnStepDevice>(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        mount.Connected.ShouldBe(true);
        // The base InitDeviceAsync sends :GVP# then :GVN#, then up to 3 :U# toggles.
        responder.ReceivedCommands.ShouldContain(":GVP#");
        responder.ReceivedCommands.ShouldContain(":GVN#");
    }

    [Fact(Timeout = 30_000)]
    public async Task GivenOnStepWifiResponderWhenIsTrackingItIssuesGuQueryAndParsesFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var responder = await FakeOnStepWifiResponder.StartAsync(ct);
        responder.SetTracking(false);

        var device = new OnStepDevice(DeviceType.Mount, "test-id", "OnStep WiFi Test", responder.Host, responder.Port);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new OnStepMountDriver<OnStepDevice>(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        (await mount.IsTrackingAsync(ct)).ShouldBe(false); // :GU# contains 'n'

        responder.SetTracking(true);
        (await mount.IsTrackingAsync(ct)).ShouldBe(true); // :GU# omits 'n'

        responder.ReceivedCommands.ShouldContain(":GU#");
    }

    [Fact(Timeout = 30_000)]
    public async Task GivenOnStepWifiResponderWhenSetTrackingItIssuesTeAndParsesAck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var responder = await FakeOnStepWifiResponder.StartAsync(ct);

        var device = new OnStepDevice(DeviceType.Mount, "test-id", "OnStep WiFi Test", responder.Host, responder.Port);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new OnStepMountDriver<OnStepDevice>(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);

        responder.ReceivedCommands.ShouldContain(":Te#");
        responder.IsTracking.ShouldBe(true);

        await mount.SetTrackingAsync(false, ct);

        responder.ReceivedCommands.ShouldContain(":Td#");
        responder.IsTracking.ShouldBe(false);
    }

    /// <summary>
    /// In-process TCP responder that speaks just enough OnStep protocol for the driver
    /// to connect and exercise basic commands. Mutable state (<see cref="IsTracking"/>,
    /// <see cref="IsParked"/>) is exposed for tests to drive scenarios.
    /// </summary>
    private sealed class FakeOnStepWifiResponder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public string Host { get; }
        public int Port { get; }
        public ConcurrentBag<string> ReceivedCommands { get; } = new();
        public bool IsTracking { get; private set; }
        public bool IsParked { get; private set; }
        public bool IsSlewing { get; private set; }

        public void SetTracking(bool value) => IsTracking = value;

        private FakeOnStepWifiResponder(TcpListener listener)
        {
            _listener = listener;
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Host = endpoint.Address.ToString();
            Port = endpoint.Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static Task<FakeOnStepWifiResponder> StartAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeOnStepWifiResponder(listener));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[256];
                    var sb = new StringBuilder();
                    while (!ct.IsCancellationRequested)
                    {
                        var n = await stream.ReadAsync(buffer, ct);
                        if (n <= 0) break;

                        for (var i = 0; i < n; i++)
                        {
                            sb.Append((char)buffer[i]);
                            if (buffer[i] == (byte)'#')
                            {
                                var command = sb.ToString();
                                sb.Clear();
                                ReceivedCommands.Add(command);

                                var response = HandleCommand(command);
                                if (response is { Length: > 0 })
                                {
                                    var bytes = Encoding.Latin1.GetBytes(response);
                                    await stream.WriteAsync(bytes, ct);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (System.IO.IOException) { }
        }

        private string? HandleCommand(string command)
        {
            switch (command)
            {
                case ":GVP#": return "On-Step#";
                case ":GVN#": return "10.30#";
                case ":U#": return null; // toggles precision, no response
                case ":GR#": return "12:00:00#"; // RA — high precision response
                case ":Gr#": return "12:00:00#";
                case ":GD#": return "+45*00:00#";
                case ":Gd#": return "+45*00:00#";
                case ":GU#":
                {
                    var sb = new StringBuilder();
                    if (!IsTracking) sb.Append('n');
                    if (!IsSlewing) sb.Append('N');
                    sb.Append(IsParked ? 'P' : 'p');
                    sb.Append('#');
                    return sb.ToString();
                }
                case ":Te#": IsTracking = true; return "1";
                case ":Td#": IsTracking = false; return "1";
                case ":hP#": IsParked = true; return "1";
                case ":hR#": IsParked = false; return "1";
                case ":hQ#": return "1";
                case ":Gm#": return IsParked ? "N" : "E";
                default: return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { await _acceptLoop; } catch { /* best-effort */ }
            _cts.Dispose();
        }
    }
}
