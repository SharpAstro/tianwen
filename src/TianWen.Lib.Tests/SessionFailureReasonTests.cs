using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the user-facing session failure surfacing: a device that fails to CONNECT at initialisation
/// fails the session fast (a flip-flat we cannot open leaves the OTA blind) with a plain-language
/// <see cref="ISession.FailureReason"/> naming the device -- not just a stack trace in the log. The
/// reason feeds the GUI notification, the hosted <c>/state</c> endpoint, and the CLI.
/// </summary>
[Collection("Session")]
public class SessionFailureReasonTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task DeviceConnectFailureAtInit_FailsSessionWithUserFacingReason()
    {
        var ct = TestContext.Current.CancellationToken;

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, coverFactory: sp => new Cover(new BrokenCoverDevice(), sp), cancellationToken: ct);

        await ctx.Session.RunAsync(ct);

        ctx.Session.Phase.ShouldBe(SessionPhase.Failed);
        var reason = ctx.Session.FailureReason.ShouldNotBeNull();
        // Plain language naming the device + what to check, not an exception dump.
        reason.ShouldStartWith("Could not connect to the cover/flat panel 'Broken Panel'");
        reason.ShouldContain("telescope may still be covered");
        reason.ShouldNotContain("Exception");
    }
}
