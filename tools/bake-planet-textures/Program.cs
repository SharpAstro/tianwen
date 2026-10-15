// Bakes planet-textures.png, the sky atlas's offline planet colour maps (docs/plans/atlas-planet-detail.md, "A4: the textured
// disc"). Usage:
//
//   dotnet run --project tools/bake-planet-textures -c Release -- <download folder> <out.png>
//
// Each source is downloaded into the folder unless it is already there, read in its own longitude convention (stated per source
// below, never shifted by hand), its holes filled where it has any, area-averaged down to the atlas's cell and written through
// PlanetTextureAtlas.Encode, the same layout PlanetTextureAtlas.Decode reads back. The report printed at the end is the review:
// the file's size and, per body, its mean colour.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using SharpAstro.Jpeg;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: BakePlanetTextures <download folder> <out.png>");
    return 2;
}

var folder = Directory.CreateDirectory(args[0]).FullName;
var output = args[1];

// The equirectangular "centred on 0" sources: their left edge is 180 degrees (east or west alike), and east longitude grows to the
// right, so their first column is at WEST longitude 180 and they run eastward. OPAL's left edge is 0 degrees west, falling to the
// right (its readme: "left edge = 0 System III W. longitude, decreasing to the right"): west 0 (360), eastward.
const double CentredOnZero = 180;
const double OpalEdge = 0;

Source[] sources =
[
    // Mercury and Mars: Solar System Scope's 2k maps (CC BY 4.0, "based on NASA elevation and imagery data"). Checked here on Mars:
    // the darkest column at +8 degrees (Syrtis Major, 69 E) sits at 0.72 of the width, where 180 + 69 = 249 of 360 is 0.69.
    new(CatalogIndex.Mercury, "https://www.solarsystemscope.com/textures/download/2k_mercury.jpg", CentredOnZero),
    // Venus: the same source's cloud deck. A cloud map has no fixed longitude; the visible disc is nearly featureless, as it is.
    new(CatalogIndex.Venus, "https://www.solarsystemscope.com/textures/download/2k_venus_atmosphere.jpg", CentredOnZero),
    // The Earth: NASA Earth Observatory's Blue Marble: Next Generation, with topography and bathymetry, July 2004, its smallest whole-world
    // JPEG (5400 x 2700, 8 km a pixel). A cloud-free day map, public domain ("please credit NASA Earth Observatory"). Plate carree centred
    // on Greenwich, east to the right; checked by PlanetTextureAtlasTests (Africa at 0, north up). One month for every date: a cell a month
    // would be eleven more cells for the seasons' snow.
    new(CatalogIndex.Earth, "https://assets.science.nasa.gov/content/dam/science/esd/eo/images/bmng/bmng-topography-bathymetry/july/world.topo.bathy.200407.3x5400x2700.jpg", CentredOnZero),
    // The Moon: NASA SVS's CGI Moon Kit, LRO LROC WAC colour with the poles filled (public domain). Centred on 0 degrees.
    new(CatalogIndex.Moon, "https://svs.gsfc.nasa.gov/vis/a000000/a004700/a004720/lroc_color_poles_1k.jpg", CentredOnZero),
    new(CatalogIndex.Mars, "https://www.solarsystemscope.com/textures/download/2k_mars.jpg", CentredOnZero),
    // The giants: OPAL's colour composites (HST, public; the acknowledgement is in the README), the same maps the runtime upgrade
    // fetches (OpalColourMaps), so the offline texture is the upgrade's own map at the atlas's size.
    Opal(CatalogIndex.Jupiter),
    Opal(CatalogIndex.Saturn),
    Opal(CatalogIndex.Uranus),
    Opal(CatalogIndex.Neptune),
];

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("TianWen bake-planet-textures (https://github.com/SharpAstro/tianwen)");

var maps = ImmutableDictionary.CreateBuilder<CatalogIndex, PlanetColourMap>();
foreach (var source in sources)
{
    var path = Path.Combine(folder, Path.GetFileName(new Uri(source.Url).LocalPath));
    if (!File.Exists(path))
    {
        Console.WriteLine($"fetch {source.Url}");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(source.Url));
    }
    var bytes = await File.ReadAllBytesAsync(path);
    PlanetColourMap full;
    if (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase))
    {
        full = OpalColourMaps.Decode(bytes) ?? throw new InvalidOperationException($"{path} is not an 8-bit RGB TIFF");
    }
    else
    {
        var jpeg = JpegDecoder.Decode(bytes);
        full = PlanetColourMap.FromRgb8(jpeg.Pixels, jpeg.Width, jpeg.Height, bytesPerPixel: 4, sourceWestAtColumn0: source.WestAtColumn0, sourceEastward: true);
    }
    maps[source.Body] = Downsampled(full, PlanetTextureAtlas.CellWidth, PlanetTextureAtlas.CellHeight);
}

var png = PlanetTextureAtlas.Encode(maps.ToImmutable());
await File.WriteAllBytesAsync(output, png);
Console.WriteLine($"wrote {output}: {png.Length:N0} bytes, {PlanetTextureAtlas.Width}x{PlanetTextureAtlas.Height}");
foreach (var (body, map) in PlanetTextureAtlas.Decode(png).OrderBy(p => PlanetTextureAtlas.Layout.IndexOf(p.Key)))
{
    Console.WriteLine($"  {body,-8} mean RGB {map.Red.MeanAlbedo:F3} {map.Green.MeanAlbedo:F3} {map.Blue.MeanAlbedo:F3}  {PlanetTextureAtlas.Credit(body)}");
}
return 0;

static Source Opal(CatalogIndex body) => OpalColourMaps.TryFor(body, out var map)
    ? new Source(body, map.Url, OpalEdge)
    : throw new InvalidOperationException($"no OPAL map for {body}");

// The map at width x height, each sample the mean of an 8 x 8 grid of bilinear samples across its cell: an area average for the 8:1
// reduction of a 2k source, and smooth for OPAL's 1.76:1.
static PlanetColourMap Downsampled(PlanetColourMap map, int width, int height)
{
    const int Sub = 8;
    var planes = new float[3][];
    PlanetMap[] source = [map.Red, map.Green, map.Blue];
    for (var p = 0; p < 3; p++)
    {
        var values = planes[p] = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var j = 0; j < Sub; j++)
                {
                    var latitude = 90 - ((y + ((j + 0.5) / Sub)) * 180.0 / height);
                    for (var i = 0; i < Sub; i++)
                    {
                        sum += source[p].Sample(latitude, 360 - ((x + ((i + 0.5) / Sub)) * 360.0 / width));
                    }
                }
                values[(y * width) + x] = (float)(sum / (Sub * Sub));
            }
        }
    }
    return new PlanetColourMap(new PlanetMap(planes[0], width, height), new PlanetMap(planes[1], width, height), new PlanetMap(planes[2], width, height));
}

/// <summary>One source map: the body, where it is served, the WEST longitude of its first column (east growing to the right), and
/// nothing else: OPAL's TIFFs are filled by OpalColourMaps.Decode, and the JPEG sources have no holes.</summary>
internal readonly record struct Source(CatalogIndex Body, string Url, double WestAtColumn0);
