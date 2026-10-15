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
    /// a hole (all three channels near black), Uranus's unseen southern hemisphere and Saturn's north pole included. The Earth is the
    /// exception the hole threshold cannot tell from a hole: its deep sea is that dark in the Blue Marble itself (a tenth of its samples
    /// sum under the threshold, the darkest 12 of 765), its source has no holes and the bake fills none, so it is held only to never
    /// being pure black.
    /// </summary>
    [Fact]
    public void TheAtlasDecodesEveryBodyWithNoHoles()
    {
        foreach (var body in PlanetTextureAtlas.Layout)
        {
            var map = Atlas(body);
            map.Width.ShouldBe(PlanetTextureAtlas.CellWidth);
            var floor = body == CatalogIndex.Earth ? 0 : PlanetColourMap.HoleThreshold;
            for (var latitude = -89.0; latitude <= 89; latitude += 2)
            {
                for (var west = 0.0; west < 360; west += 5)
                {
                    var sum = (map.Red.Sample(latitude, west) + map.Green.Sample(latitude, west) + map.Blue.Sample(latitude, west)) * 255;
                    sum.ShouldBeGreaterThan(floor, $"{body} at {latitude}, {west} W");
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

    /// <summary>
    /// The Earth's map is the right way round, checked against the geography every reader knows (the Blue Marble is a plate carree
    /// centred on Greenwich, east growing to the right, which the bake turns to <see cref="PlanetMap"/>'s WEST longitude): at 22 degrees
    /// north the Sahara (Egypt and Sudan, 30 E, so 330 W) is sand, redder than it is blue, and the Atlantic at 30 W is sea, bluer than red;
    /// Africa and Europe sit about longitude 0 and the Pacific about 180, so the land at 15 N, 0 is redder than the sea at 15 N, 180. North
    /// is up: 22 degrees SOUTH under the Sahara's 8 E is the Atlantic off Namibia, and 75 S under 0 is Antarctica's ice, brighter than the
    /// Sahara. The same checks are run on the map read mirrored east to west, flipped north to south and shifted half a turn, and each of
    /// those must fail them, so the checks can tell.
    /// </summary>
    [Fact]
    public void TheEarthMapHasAfricaAtLongitudeZeroAndNorthUp()
    {
        var earth = Atlas(CatalogIndex.Earth);
        // Mean colour over a 5 x 5 degree patch about (latitude, EAST longitude).
        (double R, double G, double B) Read(double latitude, double east)
        {
            var (r, g, b) = (0.0, 0.0, 0.0);
            for (var dy = -2.0; dy <= 2; dy += 1)
            {
                for (var dx = -2.0; dx <= 2; dx += 1)
                {
                    var west = ((-(east + dx)) % 360 + 360) % 360;
                    var at = Math.Clamp(latitude + dy, -89.5, 89.5);
                    r += earth.Red.Sample(at, west);
                    g += earth.Green.Sample(at, west);
                    b += earth.Blue.Sample(at, west);
                }
            }
            return (r / 25, g / 25, b / 25);
        }
        static double Redness((double R, double G, double B) c) => c.R - c.B;
        static double Brightness((double R, double G, double B) c) => c.R + c.G + c.B;

        // Each check as (what it says, whether it holds), read through a map that may be turned wrong.
        static (string, bool)[] Checks(Func<double, double, (double R, double G, double B)> at) =>
        [
            ("the Sahara at 22 N, 30 E is sand", Redness(at(22, 30)) > 0.1),
            ("the Atlantic at 22 N, 30 W is sea", Redness(at(22, -30)) < 0),
            ("Africa is at longitude 0, the Pacific at 180", Redness(at(15, 0)) > Redness(at(15, 180)) + 0.1),
            ("22 S under 8 E is the Atlantic off Namibia", Redness(at(-22, 8)) < 0),
            ("Antarctica's ice at 75 S is brighter than the Sahara", Brightness(at(-75, 0)) > Brightness(at(22, 30))),
        ];

        foreach (var (what, holds) in Checks(Read))
        {
            holds.ShouldBeTrue(what);
        }
        Checks((lat, east) => Read(lat, -east)).ShouldContain(c => !c.Item2, "the map read mirrored east to west passes every check");
        Checks((lat, east) => Read(-lat, east)).ShouldContain(c => !c.Item2, "the map read flipped north to south passes every check");
        Checks((lat, east) => Read(lat, east + 180)).ShouldContain(c => !c.Item2, "the map read half a turn round passes every check");
    }
}
