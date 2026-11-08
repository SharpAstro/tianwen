using System;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The orrery's part of a web atlas link (#2072, <see cref="OrreryLink"/>): a new view writes nothing but that it is open, every setting
/// that moved is written and read back as it was, and a hand-edited value that does not parse reads as absent, never as an error.
/// </summary>
public class OrreryLinkTests
{
    [Fact]
    public void ANewViewIsJustOpen()
    {
        OrreryLink.QueryFor(new OrreryState(), instantUtc: null).ShouldBe("orrery=1");
        var link = OrreryLink.Parse("?view=sky&orrery=1").ShouldNotBeNull();
        link.ShouldBe(new OrreryLinkData(null, false, null, null, false, null, null, null, null, null));
    }

    [Fact]
    public void WithoutTheOpenKeyThereIsNoOrrery()
    {
        OrreryLink.Parse("?view=sky&object=M42&focus=Mars").ShouldBeNull();
        OrreryLink.Parse("").ShouldBeNull();
        OrreryLink.Parse("?orrery=0&focus=Mars").ShouldBeNull();
    }

    [Fact]
    public void EverySettingThatMovedIsWrittenAndReadBack()
    {
        var orrery = new OrreryState
        {
            Playing = true,
            DaysPerSecond = OrreryState.PlaySpeeds[11].DaysPerSecond,
            AzimuthDeg = 123.45,
            ElevationDeg = -12.34,
            Distance = 2.5,
            Scale = OrreryScale.True,
            CometFilter = OrreryCometFilter.All,
            ShowBackdrop = false,
            Focus = CatalogIndex.Jupiter,
            Selected = CatalogIndex.Moon,
            CentredOnFocus = true,
        };
        var instant = new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.FromHours(10));

        var query = OrreryLink.QueryFor(orrery, instant);

        query.ShouldBe("orrery=1&t=2027-03-03T19%3A06%3A07Z&play=1&speed=1y&focus=Jupiter&centre=1&panel=Moon&az=123.5&el=-12.3&dist=2.5&scale=true&comets=all&backdrop=0");
        var link = OrreryLink.Parse("?view=sky&" + query).ShouldNotBeNull();
        link.Instant.ShouldBe(instant);
        link.Playing.ShouldBeTrue();
        link.DaysPerSecond.ShouldBe(OrreryState.PlaySpeeds[11].DaysPerSecond);
        (link.Focus, link.CentredOnFocus, link.Panel).ShouldBe(("Jupiter", true, "Moon"));
        link.Camera.ShouldBe((123.5, -12.3, 2.5));
        (link.Scale, link.CometFilter, link.ShowBackdrop).ShouldBe((OrreryScale.True, OrreryCometFilter.All, false));
    }

    [Fact]
    public void AClosedPanelIsNoneAndTheFocussPanelIsLeftOut()
    {
        OrreryLink.QueryFor(new OrreryState { Focus = CatalogIndex.Mars, Selected = CatalogIndex.Mars }, null).ShouldBe("orrery=1&focus=Mars");
        OrreryLink.QueryFor(new OrreryState { Focus = CatalogIndex.Mars, Selected = null }, null).ShouldBe("orrery=1&focus=Mars&panel=none");
    }

    [Fact]
    public void AValueThatDoesNotParseReadsAsAbsent()
    {
        var link = OrreryLink.Parse("?orrery=1&t=yesterday&speed=warp&az=x&el=10&dist=3&comets=7&scale=huge&backdrop=maybe&play=yes").ShouldNotBeNull();
        link.ShouldBe(new OrreryLinkData(null, false, null, null, false, null, null, null, null, null));
        // The camera is all three or nothing, and a distance must be positive; an elevation is kept off the pole.
        OrreryLink.Parse("?orrery=1&az=10&el=20").ShouldNotBeNull().Camera.ShouldBeNull();
        OrreryLink.Parse("?orrery=1&az=10&el=20&dist=0").ShouldNotBeNull().Camera.ShouldBeNull();
        OrreryLink.Parse("?orrery=1&az=10&el=95&dist=1").ShouldNotBeNull().Camera.ShouldBe((10.0, OrreryCamera.MaxElevationDeg, 1.0));
    }

    [Fact]
    public void EveryBodyTheOrreryNamesResolvesFromItsToken()
    {
        foreach (var body in new[] { CatalogIndex.Sol, CatalogIndex.Mercury, CatalogIndex.Venus, CatalogIndex.Earth, CatalogIndex.Moon,
            CatalogIndex.Mars, CatalogIndex.Jupiter, CatalogIndex.Saturn, CatalogIndex.Uranus, CatalogIndex.Neptune })
        {
            OrreryLink.TryResolveBody(OrreryLink.BodyToken(body), comets: null, out var resolved).ShouldBeTrue(body.ToString());
            resolved.ShouldBe(body);
        }
        OrreryLink.TryResolveBody("mars", null, out var lower).ShouldBeTrue();
        lower.ShouldBe(CatalogIndex.Mars);
        // A comet needs the comet set; with none, it does not resolve (and the host tries again once it has loaded).
        OrreryLink.TryResolveBody("10P", null, out _).ShouldBeFalse();
    }

    /// <summary>
    /// A comet is written as its canonical designation and read back through the comet search, so it resolves once the repository
    /// holds it: a numbered one (<c>10P</c>) and a provisional one, whose designation has a space and a slash (<c>C/2026 A1</c>),
    /// through the link's own escaping as well.
    /// </summary>
    [Theory]
    [InlineData("10P", "Tempel")]
    [InlineData("C/2026 A1", "PANSTARRS")]
    [InlineData("C/2026 A1", null)]
    public void ACometIsItsDesignationAndResolvesOnceTheRepositoryHoldsIt(string designation, string? commonName)
    {
        var comet = StubCometRepository.Comet(designation, commonName);
        var index = comet.CatalogIndex.ShouldNotBeNull();
        var repo = new StubCometRepository(StubCometRepository.Comet("12P", null), comet);

        var token = OrreryLink.BodyToken(index);
        OrreryLink.TryResolveBody(token, null, out _).ShouldBeFalse("a comet needs the set");
        OrreryLink.TryResolveBody(token, new StubCometRepository(), out _).ShouldBeFalse("an empty set holds none");
        OrreryLink.TryResolveBody(token, repo, out var resolved).ShouldBeTrue(token);
        resolved.ShouldBe(index);

        // Through the whole link: written, escaped, parsed, resolved.
        var query = OrreryLink.QueryFor(new OrreryState { Focus = index, Selected = index }, null);
        var link = OrreryLink.Parse("?" + query).ShouldNotBeNull();
        OrreryLink.TryResolveBody(link.Focus, repo, out var viaLink).ShouldBeTrue(query);
        viaLink.ShouldBe(index);
    }
}
