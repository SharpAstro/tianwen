using System;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

public class StartupTimeOverrideTests
{
    private static readonly DateTimeOffset RealNow = new(2026, 6, 21, 8, 0, 0, TimeSpan.FromHours(10));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-timestamp")]
    [InlineData("tonight")]
    public void GivenBlankOrUnparseableWhenTryParseThenFalse(string? raw)
    {
        StartupTimeOverride.TryParse(raw, RealNow, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void GivenIsoTimestampWithOffsetWhenTryParseThenOffsetIsDifferenceFromRealNow()
    {
        // Pretend it is 22:00 the same Melbourne evening while the real clock says 08:00 -> +14h.
        StartupTimeOverride.TryParse("2026-06-21T22:00:00+10:00", RealNow, out var simulatedNow, out var offset)
            .ShouldBeTrue();

        simulatedNow.ShouldBe(new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.FromHours(10)));
        offset.ShouldBe(TimeSpan.FromHours(14));
    }

    [Fact]
    public void GivenTimestampWithoutOffsetWhenTryParseThenAssumedLocal()
    {
        // No explicit offset: read as the machine's local time, so it parses without throwing and
        // yields a finite offset (exact value depends on the host time zone, so only assert shape).
        StartupTimeOverride.TryParse("2026-06-21T22:00:00", RealNow, out var simulatedNow, out var offset)
            .ShouldBeTrue();

        simulatedNow.DateTime.ShouldBe(new DateTime(2026, 6, 21, 22, 0, 0));
        offset.ShouldBe(simulatedNow - RealNow);
    }
}

public class OffsetTimeProviderTests
{
    [Fact]
    public void GivenOffsetWhenGetUtcNowThenShiftedButAdvancesAtRealRate()
    {
        var baseNow = new DateTimeOffset(2026, 6, 21, 8, 0, 0, TimeSpan.Zero);
        var inner = new FakeTimeProvider(baseNow);
        var offset = TimeSpan.FromHours(14);
        var shifted = new OffsetTimeProvider(inner, offset);

        // Anchored: now jumps forward by the offset.
        shifted.GetUtcNow().ShouldBe(baseNow + offset);

        // Real-rate: advancing the inner clock by 1h advances the shifted clock by exactly 1h.
        inner.Advance(TimeSpan.FromHours(1));
        shifted.GetUtcNow().ShouldBe(baseNow + offset + TimeSpan.FromHours(1));
    }

    [Fact]
    public void GivenZeroOffsetWhenGetUtcNowThenMatchesInner()
    {
        var baseNow = new DateTimeOffset(2026, 6, 21, 8, 0, 0, TimeSpan.Zero);
        var inner = new FakeTimeProvider(baseNow);

        new OffsetTimeProvider(inner, TimeSpan.Zero).GetUtcNow().ShouldBe(inner.GetUtcNow());
    }
}
