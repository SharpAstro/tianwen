using System.Linq;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI's two ramp rows read and step in degrees C per minute with a floor and a ceiling (#1839): the ceiling is the
/// fastest rate that changes the ramp, a degree a step, so the rows offer no setting that does nothing.
/// </summary>
public class SessionConfigGroupsRateRowsTests
{
    private static ConfigFieldDescriptor Row(string label)
        => SessionConfigGroups.Groups.SelectMany(g => g.Fields).Single(f => f.Label == label);

    [Fact]
    public void TheCooldownRowStepsHalfADegreeBetweenAHalfAndTheFastestRate()
    {
        var row = Row("Cooldown Rate");
        var config = new SessionConfiguration() with { CooldownMaxRate = 0.5 };
        var fastest = CameraCoolingRamp.FastestMaxRateCPerMinute;

        fastest.ShouldBe(4.0, "a degree per 15 s step");
        row.Decrement(config).CooldownMaxRate.ShouldBe(0.5, "the floor");
        row.Increment(config).CooldownMaxRate.ShouldBe(1.0);
        row.Increment(config with { CooldownMaxRate = fastest }).CooldownMaxRate.ShouldBe(fastest, "the ceiling");
        row.Decrement(config with { CooldownMaxRate = fastest }).CooldownMaxRate.ShouldBe(3.5);
        row.Decrement(config with { CooldownMaxRate = 10.0 }).CooldownMaxRate.ShouldBe(fastest, "a faster saved rate steps back inside");
    }

    [Fact]
    public void TheWarmupRowStepsHalfADegreeBetweenAHalfAndTheFastestRate()
    {
        var row = Row("Warmup Rate");
        var config = new SessionConfiguration() with { WarmupMaxRate = 0.5 };
        var fastest = CameraCoolingRamp.FastestMaxRateCPerMinute;

        row.Decrement(config).WarmupMaxRate.ShouldBe(0.5, "the floor");
        row.Increment(config).WarmupMaxRate.ShouldBe(1.0);
        row.Increment(config with { WarmupMaxRate = fastest }).WarmupMaxRate.ShouldBe(fastest, "the ceiling");
        row.Decrement(config with { WarmupMaxRate = fastest }).WarmupMaxRate.ShouldBe(3.5);
        row.Decrement(config with { WarmupMaxRate = 10.0 }).WarmupMaxRate.ShouldBe(fastest, "a faster saved rate steps back inside");
    }
}
