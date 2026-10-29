using System.Linq;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The GUI's two ramp rows read and step in degrees C per minute with a floor and a ceiling (#1839).</summary>
public class SessionConfigGroupsRateRowsTests
{
    private static ConfigFieldDescriptor Row(string label)
        => SessionConfigGroups.Groups.SelectMany(g => g.Fields).Single(f => f.Label == label);

    [Fact]
    public void TheCooldownRowStepsHalfADegreeBetweenAHalfAndTen()
    {
        var row = Row("Cooldown Rate");
        var config = new SessionConfiguration() with { CooldownMaxRate = 0.5 };

        row.Decrement(config).CooldownMaxRate.ShouldBe(0.5, "the floor");
        row.Increment(config).CooldownMaxRate.ShouldBe(1.0);
        row.Increment(config with { CooldownMaxRate = 10.0 }).CooldownMaxRate.ShouldBe(10.0, "the ceiling");
        row.Decrement(config with { CooldownMaxRate = 10.0 }).CooldownMaxRate.ShouldBe(9.5);
    }

    [Fact]
    public void TheWarmupRowStepsHalfADegreeBetweenAHalfAndTen()
    {
        var row = Row("Warmup Rate");
        var config = new SessionConfiguration() with { WarmupMaxRate = 0.5 };

        row.Decrement(config).WarmupMaxRate.ShouldBe(0.5, "the floor");
        row.Increment(config).WarmupMaxRate.ShouldBe(1.0);
        row.Increment(config with { WarmupMaxRate = 10.0 }).WarmupMaxRate.ShouldBe(10.0, "the ceiling");
        row.Decrement(config with { WarmupMaxRate = 10.0 }).WarmupMaxRate.ShouldBe(9.5);
    }
}
