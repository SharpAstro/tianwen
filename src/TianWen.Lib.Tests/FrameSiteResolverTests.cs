using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a photograph says it was taken, which is what decides whether a horizon can be drawn under
/// the sky behind it.
/// </summary>
public class FrameSiteResolverTests
{
    /// <summary>A site in Melbourne, in the units and sign convention SITELAT / SITELONG use.</summary>
    private static ImageMeta MetaAt(float latitude, float longitude, DateTimeOffset? capturedAt = null)
        => new ImageMeta("Test",
            capturedAt ?? new DateTimeOffset(2026, 6, 21, 12, 30, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(120), FrameType.Light, "Test",
            3.76f, 3.76f, 700, -1, Filter.None, 1, 1,
            -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, latitude, longitude);

    [Fact]
    public void AFrameThatStatesItsSite_IsBelievedToTheMetre()
    {
        var site = FrameSiteResolver.FromHeader(MetaAt(-37.876389f, 145.177778f));

        site.Source.ShouldBe(FrameSiteSource.Header);
        site.LatitudeDeg.ShouldBe(-37.876389, 1e-4);
        site.LongitudeDeg.ShouldBe(145.177778, 1e-4);
        site.IsKnown.ShouldBeTrue();
    }

    /// <summary>Some software writes east longitude as 0..360; both spellings mean the same place.</summary>
    [Fact]
    public void ALongitudePastOneEighty_IsBroughtBackIntoTheSignedRange()
    {
        FrameSiteResolver.FromHeader(MetaAt(-37.87f, 214.82f)).LongitudeDeg.ShouldBe(-145.18, 0.01);
    }

    [Fact]
    public void AFrameWithNoSiteCards_KnowsNothing()
    {
        FrameSiteResolver.FromHeader(MetaAt(float.NaN, float.NaN)).IsKnown.ShouldBeFalse();
    }

    /// <summary>
    /// The one that matters in practice: capture software writes the profile's site whether or not
    /// anyone filled it in, so a frame from an unconfigured profile carries (0, 0). Believing it draws
    /// a confident horizon for a point in the Atlantic.
    /// </summary>
    [Fact]
    public void AnUnfilledProfilesZeroZero_IsReadAsUnsetRatherThanAsTheGulfOfGuinea()
    {
        FrameSiteResolver.FromHeader(MetaAt(0f, 0f)).IsKnown.ShouldBeFalse();

        // A real site that happens to sit on ONE of the two zeros is still a site: Greenwich is on the
        // prime meridian and the equator crosses plenty of observing sites.
        FrameSiteResolver.FromHeader(MetaAt(51.4769f, 0f)).IsKnown.ShouldBeTrue();
        FrameSiteResolver.FromHeader(MetaAt(0f, 145.18f)).IsKnown.ShouldBeTrue();
    }

    [Fact]
    public void AnImpossibleLatitude_IsRefused()
    {
        FrameSiteResolver.FromHeader(MetaAt(120f, 10f)).IsKnown.ShouldBeFalse();
    }

    /// <summary>
    /// Stepping through a folder where one frame carries the cards and the next does not: the horizon
    /// stays, and the answer says it belongs to a different frame.
    /// </summary>
    [Fact]
    public void AFrameWithNoSite_KeepsTheOneTheLastFrameHad_AndSaysSo()
    {
        var known = FrameSiteResolver.FromHeader(MetaAt(-37.876389f, 145.177778f));

        var carried = FrameSiteResolver.Resolve(MetaAt(float.NaN, float.NaN), known);

        carried.Source.ShouldBe(FrameSiteSource.Remembered);
        carried.LatitudeDeg.ShouldBe(known.LatitudeDeg);
        carried.Describe().ShouldBe("site from a previous frame");

        // And a frame that DOES know outranks what was carried, rather than the memory sticking.
        var ownSite = FrameSiteResolver.Resolve(MetaAt(51.4769f, -0.0005f), carried);
        ownSite.Source.ShouldBe(FrameSiteSource.Header);
        ownSite.LatitudeDeg.ShouldBe(51.4769, 1e-4);
    }

    [Fact]
    public void RememberingSomethingAlreadyRemembered_DoesNotPromoteIt()
    {
        FrameSite.Unknown.AsRemembered().Source.ShouldBe(FrameSiteSource.None);

        var twice = FrameSiteResolver.FromHeader(MetaAt(-37.87f, 145.18f)).AsRemembered().AsRemembered();
        twice.Source.ShouldBe(FrameSiteSource.Remembered);
    }

    /// <summary>
    /// A missing DATE-OBS parses to year 1 rather than to a null, so "does this frame know when it was
    /// taken" is a plausibility test rather than a null check.
    /// </summary>
    [Fact]
    public void AFrameWithNoCaptureTime_DoesNotClaimOne()
    {
        var meta = MetaAt(-37.87f, 145.18f, new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero));

        FrameSiteResolver.CapturedAt(meta).ShouldBeNull();
    }

    [Fact]
    public void AFrameWithACaptureTime_ReportsIt()
    {
        var when = new DateTimeOffset(2026, 9, 10, 14, 5, 0, TimeSpan.Zero);

        FrameSiteResolver.CapturedAt(MetaAt(-37.87f, 145.18f, when)).ShouldBe(when);
    }
}
