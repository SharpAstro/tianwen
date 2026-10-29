using Shouldly;
using System;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The settle rule for a sensor read as ambient before a cool-down (#1842): steady over a minute, no trend, and a
/// reading that cannot be read breaks the window. Pure, so every time here is made up.
/// </summary>
public class SensorAmbientSamplerTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    // Feeds one sample per step for the given span, temperature a function of seconds since the start.
    private static SensorAmbientSampler Feed(TimeSpan span, Func<double, double> tempAtSeconds, SensorAmbientSampler? sampler = null, TimeSpan? from = null)
    {
        sampler ??= new SensorAmbientSampler();
        for (var t = from ?? TimeSpan.Zero; t <= span; t += Step)
        {
            sampler.Add(T0 + t, tempAtSeconds(t.TotalSeconds));
        }
        return sampler;
    }

    [Fact]
    public void ASteadySensorIsNotSettledBeforeTheWindowAndIsAfterIt()
    {
        var sampler = Feed(TimeSpan.FromSeconds(55), static _ => 12.1);
        sampler.Settled.ShouldBeFalse("under a minute of samples is not a window");
        sampler.Current.ShouldNotBeNull().TempC.ShouldBe(12.1, 1e-9);

        sampler.Add(T0 + TimeSpan.FromSeconds(60), 12.1).ShouldBeTrue();
        var reading = sampler.Current.ShouldNotBeNull();
        reading.Settled.ShouldBeTrue();
        reading.TempC.ShouldBe(12.1, 1e-9);
        reading.AtUtc.ShouldBe(T0 + TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ANoisySensorWithinTheSpreadIsSettledAtItsMean()
    {
        // +-0.05 C jitter, no trend: a real sensor's read noise.
        var sampler = Feed(TimeSpan.FromSeconds(90), static s => 12.0 + (Math.Round(s / 5) % 2 == 0 ? 0.05 : -0.05));

        sampler.Settled.ShouldBeTrue();
        sampler.Current.ShouldNotBeNull().TempC.ShouldBe(12.0, 0.03);
    }

    [Fact]
    public void ASensorStillMovingIsNeverSettledButKeepsAReading()
    {
        // A camera just in from the sun: 0.6 C a minute.
        var sampler = Feed(TimeSpan.FromSeconds(180), static s => 16.0 - 0.6 * s / 60);

        sampler.Settled.ShouldBeFalse();
        sampler.WindowSpreadC.ShouldBeGreaterThan(SensorAmbientSampler.MaxSpreadC);
        sampler.Current.ShouldNotBeNull().Settled.ShouldBeFalse("recorded, so the warm-up knows it was not trusted");
    }

    [Fact]
    public void ASlowTrendInsideTheSpreadIsRefused()
    {
        // 0.15 C over the window passes the spread test (0.2 C) but is 0.15 C a minute: still warming or cooling.
        var sampler = Feed(TimeSpan.FromSeconds(120), static s => 12.0 + 0.15 * s / 60);

        sampler.WindowSpreadC.ShouldBeLessThanOrEqualTo(SensorAmbientSampler.MaxSpreadC);
        sampler.Settled.ShouldBeFalse();
    }

    [Fact]
    public void AMovingSensorSettlesOnceItHasBeenSteadyForAWindow()
    {
        var sampler = Feed(TimeSpan.FromSeconds(120), static s => s < 60 ? 16.0 - 0.1 * s : 10.0);

        sampler.Settled.ShouldBeTrue("the last minute is flat");
        sampler.Current.ShouldNotBeNull().TempC.ShouldBe(10.0, 0.05);
    }

    [Fact]
    public void AnUnreadableSampleBreaksTheWindow()
    {
        var sampler = Feed(TimeSpan.FromSeconds(60), static _ => 12.0);
        sampler.Settled.ShouldBeTrue();

        sampler.Add(T0 + TimeSpan.FromSeconds(65), double.NaN).ShouldBeFalse();
        sampler.Current.ShouldBeNull("what was seen before says nothing about after");

        Feed(TimeSpan.FromSeconds(130), static _ => 12.0, sampler, from: TimeSpan.FromSeconds(70));
        sampler.Settled.ShouldBeTrue("a full window of its own after the break");
        sampler.Current.ShouldNotBeNull().AtUtc.ShouldBe(T0 + TimeSpan.FromSeconds(130));
    }

    [Fact]
    public void AWindowThatHadNoTimeToFillIsNotSettledWhateverItReads()
    {
        var sampler = new SensorAmbientSampler();
        sampler.Add(T0, 12.0).ShouldBeFalse();
        sampler.Add(T0 + TimeSpan.FromSeconds(30), 12.0).ShouldBeFalse();
    }
}
