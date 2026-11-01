using Shouldly;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Alpaca;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Alpaca discovery ends at its answer window on the injected clock. The window is set to an hour, nobody answers the
/// broadcast, and the fake clock is advanced by that hour ONCE, as soon as the window's timer exists (it is made after the
/// broadcast is sent, so the test waits for the timer and not for the datagram): on the wall clock the discovery would hold for
/// the hour and the test's own timeout would end it instead. The ImageBytes download's budget has its own test beside the
/// others of that client (<see cref="AlpacaImageDownloadTests"/>).
/// </summary>
public class AlpacaDeadlineTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    [Fact(Timeout = 30_000)]
    public async Task ABroadcastNobodyAnswersEndsAtItsAnswerWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)silent.Client.LocalEndPoint!;
        var clock = new FakeTimeProviderWrapper();

        var found = AlpacaDeviceSource.DiscoverServersAsync(new IPEndPoint(IPAddress.Loopback, 0), target, clock, Long, ct);
        var asked = await silent.ReceiveAsync(ct);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        found.IsCompleted.ShouldBeFalse("nobody has answered and the clock has not moved");
        clock.Advance(Long);

        (await found).ShouldBeEmpty("nothing answered");
        Encoding.ASCII.GetString(asked.Buffer).ShouldBe("alpacadiscovery1", "the broadcast was sent before the window opened");
    }
}
