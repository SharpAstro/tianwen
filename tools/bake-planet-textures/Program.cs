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
// The Sun (#1608): GENERATED, not downloaded. See QuietSun.
maps[CatalogIndex.Sol] = QuietSun(PlanetTextureAtlas.CellWidth, PlanetTextureAtlas.CellHeight);

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

// The Sun's map: a QUIET photosphere, generated, since a real map would put a date's sunspots on every date. No observed map is
// worth its claim at this size anyway: one sample spans 2 pi 695,700 / 256 = 17,075 km of the equator by 17,075 km of latitude,
// about 170 granules of some 1,300 km, so the granulation's rms contrast in the visible (of order 10 to 15 % at the scale of one
// granule) averages to about a percent, and supergranulation shows in the continuum not at all. So: one colour, a 5772 K black body
// (the IAU 2015 nominal effective temperature, Resolution B3) through the CIE 1931 2-degree functions (Wyman, Sloan and Shirley's
// 2013 multi-lobe fit) into sRGB, its brightest channel 1; times, per sample, 1 + a Gaussian of 0.12 / sqrt(granules in the sample),
// the residual granulation, capped at three times the equator's where a sample narrows toward the pole. Seeded, so a re-bake is the
// same file. No limb darkening here: that is the renderers' (PlanetaryRender.LimbDarkening), a function of the view, not the surface.
static PlanetColourMap QuietSun(int width, int height)
{
    const double TemperatureK = 5772;
    const double GranuleContrast = 0.12;
    const double GranulesAtEquator = 17075.0 * 17075.0 / (1300.0 * 1300.0);

    // Planck's law (the constants cancel in the normalisation) through the CIE functions, 1 nm steps over 360 to 830 nm.
    double x = 0, y = 0, z = 0;
    for (var nm = 360.0; nm <= 830; nm += 1)
    {
        var metres = nm * 1e-9;
        var radiance = 1 / (Math.Pow(metres, 5) * (Math.Exp(1.438776877e-2 / (metres * TemperatureK)) - 1));
        x += radiance * ((1.056 * Lobe(nm, 599.8, 37.9, 31.0)) + (0.362 * Lobe(nm, 442.0, 16.0, 26.7)) - (0.065 * Lobe(nm, 501.1, 20.4, 26.2)));
        y += radiance * ((0.821 * Lobe(nm, 568.8, 46.9, 40.5)) + (0.286 * Lobe(nm, 530.9, 16.3, 31.1)));
        z += radiance * ((1.217 * Lobe(nm, 437.0, 11.8, 36.0)) + (0.681 * Lobe(nm, 459.0, 26.0, 13.8)));
    }
    double[] linear =
    [
        (3.2406 * x) - (1.5372 * y) - (0.4986 * z),
        (-0.9689 * x) + (1.8758 * y) + (0.0415 * z),
        (0.0557 * x) - (0.2040 * y) + (1.0570 * z),
    ];
    var peak = linear.Max();
    var colour = linear.Select(c => Encode(c / peak)).ToArray();
    Console.WriteLine($"  Sun: a {TemperatureK} K black body in sRGB is {colour[0]:F4} {colour[1]:F4} {colour[2]:F4}");

    var random = new Random(1608);
    var planes = new[] { new float[width * height], new float[width * height], new float[width * height] };
    for (var row = 0; row < height; row++)
    {
        var latitude = (90 - ((row + 0.5) * 180.0 / height)) * Math.PI / 180;
        var granules = GranulesAtEquator * Math.Cos(latitude);
        var sigma = Math.Min(GranuleContrast / Math.Sqrt(granules), 3 * GranuleContrast / Math.Sqrt(GranulesAtEquator));
        for (var column = 0; column < width; column++)
        {
            // Box-Muller: one standard normal from two uniforms.
            var normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            var factor = 1 + (sigma * normal);
            for (var p = 0; p < 3; p++)
            {
                planes[p][(row * width) + column] = (float)Math.Clamp(colour[p] * factor, 0, 1);
            }
        }
    }
    return new PlanetColourMap(new PlanetMap(planes[0], width, height), new PlanetMap(planes[1], width, height), new PlanetMap(planes[2], width, height));

    // One lobe of the fit: a Gaussian about mu, its width sigma1 below and sigma2 above.
    static double Lobe(double nm, double mu, double sigma1, double sigma2)
    {
        var t = (nm - mu) / (nm < mu ? sigma1 : sigma2);
        return Math.Exp(-0.5 * t * t);
    }

    // The sRGB transfer function.
    static double Encode(double c) => c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
}

/// <summary>One source map: the body, where it is served, the WEST longitude of its first column (east growing to the right), and
/// nothing else: OPAL's TIFFs are filled by OpalColourMaps.Decode, and the JPEG sources have no holes.</summary>
internal readonly record struct Source(CatalogIndex Body, string Url, double WestAtColumn0);
