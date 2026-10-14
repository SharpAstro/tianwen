using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky atlas's offline planet textures (docs/plans/atlas-planet-detail.md, "A4: the textured disc"): the committed
/// <c>planet-textures.png</c> decodes to every textured body, its longitudes are the maps' own, and its holes are filled.
/// </summary>
public sealed class PlanetTextureAtlasTests
{
    private static PlanetColourMap Atlas(CatalogIndex body)
    {
        var maps = PlanetTextureAtlas.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName)));
        maps.Count.ShouldBe(PlanetTextureAtlas.Layout.Length, "the atlas beside the tests decodes to every body");
        return maps[body];
    }

    /// <summary>
    /// The atlas decodes to every body, round-trips through its own encoder to the 8-bit sample, and is filled: no sample of any body is
    /// a hole (all three channels near black), Uranus's unseen southern hemisphere and Saturn's north pole included.
    /// </summary>
    [Fact]
    public void TheAtlasDecodesEveryBodyWithNoHoles()
    {
        foreach (var body in PlanetTextureAtlas.Layout)
        {
            var map = Atlas(body);
            map.Width.ShouldBe(PlanetTextureAtlas.CellWidth);
            for (var latitude = -89.0; latitude <= 89; latitude += 2)
            {
                for (var west = 0.0; west < 360; west += 5)
                {
                    var sum = (map.Red.Sample(latitude, west) + map.Green.Sample(latitude, west) + map.Blue.Sample(latitude, west)) * 255;
                    sum.ShouldBeGreaterThan(PlanetColourMap.HoleThreshold, $"{body} at {latitude}, {west} W");
                }
            }
        }
    }

    /// <summary>
    /// OPAL's maps are in System III WEST longitude, left edge 0 and falling to the right, which is <see cref="PlanetMap"/>'s own
    /// convention: the Great Red Spot (the reddest feature at 22 degrees south) reads in the atlas's Jupiter, the 2025b map of
    /// 2025-12-11, at a System III longitude that is its System II one at that date. The GRS sat near System II 70 to 80 degrees in late
    /// 2025 (drifting about +1.5 degrees a month since its 50s of 2024); the window allows 20 degrees either side of 75. Read the other
    /// way round (east longitude) the same spot would be at System II 157, and a map a year older would put it at 176.
    /// </summary>
    [Fact]
    public void TheGreatRedSpotIsWhereItsSystemIILongitudePutsIt()
    {
        var jupiter = Atlas(CatalogIndex.Jupiter);
        var reddest = 0.0;
        var best = double.MinValue;
        for (var west = 0.0; west < 360; west += 1)
        {
            var redness = 0.0;
            for (var latitude = -25.0; latitude <= -19; latitude += 1)
            {
                redness += jupiter.Red.Sample(latitude, west) - jupiter.Blue.Sample(latitude, west);
            }
            if (redness > best)
            {
                best = redness;
                reddest = west;
            }
        }

        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, new DateTimeOffset(2025, 12, 11, 12, 0, 0, TimeSpan.Zero));
        var systemII = ((reddest - (aspect.CentralMeridianIII - aspect.CentralMeridianII)) % 360 + 360) % 360;
        systemII.ShouldBe(75, 20, $"the reddest spot at 22 S is at System III {reddest} W");
    }
}
