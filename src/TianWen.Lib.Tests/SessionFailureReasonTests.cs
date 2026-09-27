using Shouldly;
using System.Collections.Generic;
using System.Linq;
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

        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, coverFactory: sp => new Cover(new BrokenCoverDevice(), sp), cancellationToken: ct);

        await ctx.Session.RunAsync(ct);

        ctx.Session.Phase.ShouldBe(SessionPhase.Failed);
        var reason = ctx.Session.FailureReason.ShouldNotBeNull();
        // Plain language naming the device + what to check, not an exception dump.
        reason.ShouldStartWith("Could not connect to the cover/flat panel 'Broken Panel'");
        reason.ShouldContain("telescope may still be covered");
        reason.ShouldNotContain("Exception");
    }

    /// <summary>
    /// A SkyWatcher mount keeps no site of its own, and here neither the profile nor the request names
    /// one, so the run has no site. Initialisation must refuse it in words, before any camera cools,
    /// rather than slew to Dec NaN at rough focus and report "Declination must be in [-90..90]" (#994).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARunWithNoSiteIsRefusedAtInitialisationBeforeCooling()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, mountPort: "SkyWatcher", cancellationToken: ct);
        var phases = new List<SessionPhase>();
        ctx.Session.PhaseChanged += (_, e) => phases.Add(e.NewPhase);

        await ctx.Session.RunAsync(ct);

        ctx.Session.Phase.ShouldBe(SessionPhase.Failed);
        ctx.Session.FailureReason.ShouldBe(Session.NoSiteReason);
        ctx.Session.FailureReason.ShouldContain("Set the site's latitude and longitude in the profile");
        phases.ShouldNotContain(SessionPhase.Cooling);
        phases.ShouldNotContain(SessionPhase.RoughFocus);
        // Straight from initialising to failed: nothing after initialisation ran.
        phases.TakeWhile(p => p is not SessionPhase.Finalising).ShouldBe([SessionPhase.Initialising, SessionPhase.Failed]);
    }
}
