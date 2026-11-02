using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
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

    [Fact(Timeout = 30_000)]
    public async Task GivenOnStepWifiResponderWhenAReplyIsLostTheQueryEndsAtTheReplyDeadlineOnTheFakeClock()
    {
        // The reply deadline runs on the driver's injected clock: a :GU# the mount never answers ends when the fake clock
        // passes the deadline, and the driver then clears the mount's command buffer with a bare #. On the wall clock the
        // query would hold for 25 s of real time whatever the fake clock did.
        var ct = TestContext.Current.CancellationToken;
        await using var responder = await FakeOnStepWifiResponder.StartAsync(ct);

        var device = new OnStepDevice(DeviceType.Mount, "test-id", "OnStep WiFi Test", responder.Host, responder.Port);
        var timeProvider = new FakeTimeProviderWrapper();
        var fakeExternal = new FakeExternal(outputHelper, timeProvider);
        await using var mount = new OnStepMountDriver<OnStepDevice>(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);
        var clearsBefore = responder.ReceivedCommands.Count(c => c is "#");
        responder.Silent.TryAdd(":GU#", 0);

        var query = mount.IsTrackingAsync(ct).AsTask();
        await timeProvider.PumpUntilCompletedAsync(query, MeadeLX200ProtocolMountDriverBase<OnStepDevice>.ReplyTimeout * 2, cancellationToken: ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(query);
        ex.Message.ShouldContain(":GU#");
        // The driver writes the # and throws at once, while the responder reads it on its own socket loop: wait for it
        // there (bounded by the test's timeout), or the count is read before the # arrives, as it once was on CI.
        await responder.WaitUntilAsync(() => responder.ReceivedCommands.Count(c => c is "#") > clearsBefore, ct);
        responder.ReceivedCommands.Count(c => c is "#").ShouldBe(clearsBefore + 1, "a lost reply clears the mount's command buffer");
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

        /// <summary>Released once per command received, so a test can wait for the responder to have read one.</summary>
        private readonly SemaphoreSlim _received = new(0);

        /// <summary>Waits until <paramref name="condition"/> holds over what was received, re-checking as each command arrives.</summary>
        public async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
            {
                await _received.WaitAsync(cancellationToken);
            }
        }

        /// <summary>Commands received and never answered, as a mount that lost them would.</summary>
        public ConcurrentDictionary<string, byte> Silent { get; } = new();
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
                                _received.Release();

                                var response = Silent.ContainsKey(command) ? null : HandleCommand(command);
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
                case ":GR#": return "12:00:00#"; // RA; high precision response
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
                case ":GW#": return IsTracking ? "GT0#" : "GN0#"; // terminated, as OnStep sends it
                // The clock a connect sets and reads back: an OnStep at UTC, acknowledging :SC and :SL with 1.
                case ":GG#": return "+00#";
                case ":GL#": return System.DateTime.UtcNow.ToString("HH:mm:ss#", System.Globalization.CultureInfo.InvariantCulture);
                case ":GC#": return System.DateTime.UtcNow.ToString("MM/dd/yy#", System.Globalization.CultureInfo.InvariantCulture);
                case [':', 'S', 'C', .., '#']: return "1";
                case [':', 'S', 'L', .., '#']: return "1";
                // The site the clock's UTC offset is looked up from (Greenwich, so it is the zone's own), and the offset
                // set when the mount's differs from it.
                case ":Gt#": return "+51*28#";
                case ":Gg#": return "+000*00#";
                case [':', 'S', 'G', .., '#']: return "1";
                // The configuration a connect reads for the top move rate: steps a degree, then microseconds a step, as a
                // harmonic OnStep reports them. A real OnStep answers both; silence would hold the connect to its deadline.
                case ":GXE6#": return "11378.0#";
                case ":GX92#": return "16#";
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
