using System;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>new SessionConfiguration()</c> is the declared defaults (P0b item 10 of
/// docs/plans/hardware-in-the-server.md, #752). It used to be the struct's zero-initialiser, since a record
/// struct whose primary constructor has required parameters gets an implicit parameterless constructor that
/// sets every field to zero, the 51 declared defaults included. Every session started over the API ran on
/// that: the site 0, 0 (synced to the mount), autofocus 0 steps over a 0 range, no warm-up at the end, 0 flats.
/// </summary>
public class SessionConfigurationDefaultsTests
{
    [Fact]
    public void TheParameterlessConstructorIsEveryDeclaredDefault()
    {
        // The primary constructor given only the nine values that have no default: the compiler fills the
        // other 51 from their declarations, so record equality pins every one of them at once.
        var declared = new SessionConfiguration(
            SetpointCCDTemperature: new SetpointTemp(-10, SetpointTempKind.Normal),
            CooldownRampInterval: TimeSpan.FromMinutes(5),
            WarmupRampInterval: TimeSpan.FromMinutes(5),
            MinHeightAboveHorizon: 20,
            DitherPixel: 5.0,
            SettlePixel: 1.0,
            DitherEveryNthFrame: 3,
            SettleTime: TimeSpan.FromSeconds(10),
            GuidingTries: 3);

        new SessionConfiguration().ShouldBe(declared);
    }

    [Fact]
    public void ADefaultConfigurationLeavesTheSiteToTheMount()
    {
        var config = new SessionConfiguration();

        double.IsNaN(config.SiteLatitude).ShouldBeTrue("a zero site synced every API session's mount to latitude 0, longitude 0");
        double.IsNaN(config.SiteLongitude).ShouldBeTrue();
        config.WarmCamerasOnSessionEnd.ShouldBeTrue();
        config.AutoFocusStepCount.ShouldBe(9);
        config.UnattendedPromptResponse.ShouldBe(UnattendedPromptResponse.Decline);
    }
}
