using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The orrery's bodies ray-traced on a real device (<c>planet_body.frag</c>, <see cref="VkPlanetBodies"/>; docs/plans/atlas-planet-detail.md,
/// A5, "The bodies on the GPU") against the CPU render they replace, <see cref="PlanetaryRender.RenderRgba"/> of the same
/// <see cref="PlanetView"/>, which stays the reference: Saturn at several ring tilts and Sun angles (the near rings over the globe, the
/// globe's shadow across them, their shadow on it, the dark face), a crescent Venus, the Moon, Jupiter's Great Red Spot (the longitude),
/// Mars gibbous and the Sun. Both draw the disc onto black at one place and size; the GPU's frame is compared with the CPU's coverage
/// times its clamped colour, byte for byte.
/// </summary>
[Collection("UI")]
public sealed class PlanetBodyGpuTests(VkSkyMapGpuFixture gpu, ITestOutputHelper output) : IClassFixture<VkSkyMapGpuFixture>
{
    private const int Width = VkSkyMapGpuFixture.Width;
    private const int Height = VkSkyMapGpuFixture.Height;
    private const float CentreX = 255.3f;
    private const float CentreY = 247.8f;

    private static readonly ImmutableDictionary<CatalogIndex, PlanetColourMap> Maps =
        PlanetTextureAtlas.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName)));

    /// <summary>
    /// A body seen from <paramref name="observerLatDeg"/> above its equator at central meridian <paramref name="meridianDeg"/>, the Sun
    /// <paramref name="sunAzimuthDeg"/> round the pole from the observer and <paramref name="sunLatDeg"/> above the equator, the screen
    /// rolled <paramref name="rollDeg"/> from the pole's own up.
    /// </summary>
    internal static PlanetView View(CatalogIndex body, double observerLatDeg, double meridianDeg, double sunLatDeg, double sunAzimuthDeg, double rollDeg)
    {
        static Vector3 Direction(double latDeg, double westDeg)
        {
            // A west longitude L lies at body-frame angle -L from the prime meridian.
            var (lat, lon) = (latDeg * Math.PI / 180, -westDeg * Math.PI / 180);
            return new Vector3((float)(Math.Cos(lat) * Math.Cos(lon)), (float)(Math.Cos(lat) * Math.Sin(lon)), (float)Math.Sin(lat));
        }
        var toward = Direction(observerLatDeg, meridianDeg);
        var sun = Direction(sunLatDeg, meridianDeg - sunAzimuthDeg);
        var up = Vector3.Normalize(Vector3.UnitZ - (Vector3.Dot(Vector3.UnitZ, toward) * toward));
        var right = Vector3.Cross(up, toward);
        var (sinRoll, cosRoll) = Math.SinCos(rollDeg * Math.PI / 180);
        return PlanetView.InBodyFrame(body, ((float)cosRoll * right) + ((float)sinRoll * up), toward, sun);
    }

    public static TheoryData<string, CatalogIndex, double, double, double, double, double, float> Cases => new()
    {
        { "saturn-open-north", CatalogIndex.Saturn, 26.7, 40, 25.0, 6, 0, 60 },
        { "saturn-open-south-rolled", CatalogIndex.Saturn, -12, 200, -10, -6, 30, 60 },
        { "saturn-nearly-edge-on", CatalogIndex.Saturn, 3, 120, 5, 4, -15, 70 },
        { "saturn-dark-face", CatalogIndex.Saturn, 15, 300, -5, 8, 10, 60 },
        { "saturn-quadrature-shadows", CatalogIndex.Saturn, 20, 10, 18, 70, 0, 55 },
        { "venus-crescent", CatalogIndex.Venus, 2, 80, -1, 150, 20, 90 },
        { "moon-gibbous", CatalogIndex.Moon, 4, 0, 1, 60, -25, 100 },
        { "jupiter-red-spot", CatalogIndex.Jupiter, 1, 321, 0.5, 10, 0, 110 },
        { "mars-gibbous", CatalogIndex.Mars, 15, 60, 20, 40, 45, 100 },
        { "sun", CatalogIndex.Sol, 7, 0, 0, 0, 0, 100 },
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Cases))]
    public async Task TheGpuDiscMatchesTheCpuRender(string name, CatalogIndex body, double observerLat, double meridian, double sunLat,
        double sunAzimuth, double roll, float radius)
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var ct = TestContext.Current.CancellationToken;
        var view = View(body, observerLat, meridian, sunLat, sunAzimuth, roll);
        Maps.TryGetValue(body, out var map);
        var flat = new RGBAColor32(0xFF, 0xE0, 0xA0, 0xFF);
        var gpuFrame = RenderGpu(view, map, flat, null, radius);
        var cpuFrame = RenderCpu(view, map, flat, radius, null);

        var (worst, mean, over) = Compare(gpuFrame, cpuFrame);
        var covered = 0;
        for (var i = 0; i < cpuFrame.Length; i += 4)
        {
            covered += cpuFrame[i] + cpuFrame[i + 1] + cpuFrame[i + 2] > 0 ? 1 : 0;
        }
        output.WriteLine($"{name}: phase {view.ToAspect(default).PhaseAngle:F1} deg, {covered} covered px, worst {worst} of 255, mean {mean:F3}, {over} channel values more than 4 off");
        await WriteAsync(name, gpuFrame, cpuFrame, ct);

        covered.ShouldBeGreaterThan(1000, "the CPU render drew no disc to compare with");
        // Measured on an Adreno X1-85: mean 0.004 at most, at most 3 values more than 4 off, the worst 16, all on a shadow's edge, where a
        // ray landing either side of it in float or double precision moves one sub-pixel. A longitude of the wrong sign, or the rings
        // without the globe's shadow, puts 805 to 65,331 values more than 4 off; the shadow alone, 5,684 on the quadrature case.
        mean.ShouldBeLessThan(0.02, $"{name}: the GPU's disc is not the CPU's");
        over.ShouldBeLessThanOrEqualTo(12, $"{name}: the GPU's disc is not the CPU's");
        worst.ShouldBeLessThanOrEqualTo(48);
    }

    /// <summary>Night draws the luminance in the palette's tint: with a tint of no blue, no pixel of a ringed Saturn has any.</summary>
    [Fact(Timeout = 120_000)]
    public async Task NightHasNoBlue()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var view = View(CatalogIndex.Saturn, 26.7, 40, 25.0, 6, 0);
        var tint = new RGBAColor32(0xE0, 0x30, 0x00, 0xFF);
        var frame = RenderGpu(view, Maps[CatalogIndex.Saturn], default, tint, 60);
        var cpu = RenderCpu(view, Maps[CatalogIndex.Saturn], default, 60, tint);
        var (lit, blue) = (0, 0);
        for (var i = 0; i < frame.Length; i += 4)
        {
            lit += frame[i] > 20 ? 1 : 0;
            blue += frame[i + 2] != 0 ? 1 : 0;
        }
        var (_, mean, _) = Compare(frame, cpu);
        output.WriteLine($"Night: {lit} lit px, {blue} with any blue, mean difference from the CPU's {mean:F3}");
        await WriteAsync("saturn-night", frame, cpu, TestContext.Current.CancellationToken);
        lit.ShouldBeGreaterThan(1000);
        blue.ShouldBe(0);
        mean.ShouldBeLessThan(0.02);
    }

    /// <summary>The shader's ring table is <see cref="SaturnRings.Main"/>'s: every edge, level and optical depth, in order.</summary>
    [Fact]
    public void TheShadersRingTableIsSaturnRingsMain()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "planet_body.frag"));
        var rows = Regex.Matches(source, @"vec4\(([0-9.]+), ([0-9.]+), ([0-9.]+), ([0-9.]+)\)");
        var rings = SaturnRings.Main.Rings;
        rows.Count.ShouldBe(rings.Length);
        for (var i = 0; i < rings.Length; i++)
        {
            var row = rows[i].Groups;
            double.Parse(row[1].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(rings[i].InnerKm);
            double.Parse(row[2].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(rings[i].OuterKm);
            double.Parse(row[3].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(rings[i].Level);
            double.Parse(row[4].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(rings[i].OpticalDepth);
        }
        source.ShouldContain($"SaturnEquatorialKm = {SaturnRings.Main.OuterKm / SaturnRings.Main.OuterRadii:F1}");
    }

    /// <summary>
    /// The browser's trace is the desktop's, word for word: the text between the two "The trace" markers of <c>planet_body.frag</c> and
    /// of <c>WebGlPlanetBodies.cs</c>'s fragment source, line by line once each line's indentation is dropped. A fix made in one and not
    /// the other fails here, which is what the parity above cannot see for the copy no test can run.
    /// </summary>
    [Fact]
    public void TheWebGlTraceIsTheVulkanTrace()
    {
        static string[] Trace(string fileName)
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Shaders", fileName));
            var start = Array.FindIndex(lines, l => l.Contains("// ---- The trace", StringComparison.Ordinal));
            var end = Array.FindIndex(lines, l => l.Contains("// ---- End of the trace", StringComparison.Ordinal));
            start.ShouldBeGreaterThanOrEqualTo(0, $"{fileName} has no start marker");
            end.ShouldBeGreaterThan(start, $"{fileName} has no end marker");
            return [.. lines[start..end].Select(l => l.Trim())];
        }
        var vulkan = Trace("planet_body.frag");
        var webGl = Trace("WebGlPlanetBodies.cs.txt");
        vulkan.Length.ShouldBeGreaterThan(100);
        for (var i = 0; i < Math.Min(vulkan.Length, webGl.Length); i++)
        {
            webGl[i].ShouldBe(vulkan[i], $"line {i + 1} of the trace");
        }
        webGl.Length.ShouldBe(vulkan.Length);
    }

    private byte[] RenderGpu(in PlanetView view, PlanetColourMap? map, RGBAColor32 flat, RGBAColor32? night, float radius)
    {
        var copy = view;
        return gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx.ShouldNotBeNull();
            var renderer = gpu.Renderer.ShouldNotBeNull();
            using var bodies = new VkPlanetBodies(ctx, () => { });
            // The first frame records the map's upload; the second draws with it.
            for (var frame = 0; frame < 2; frame++)
            {
                renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
                bodies.Draw(renderer, copy, CentreX, CentreY, radius, map, 1, flat, night).ShouldBe(frame == 1);
                renderer.EndOffscreenFrame();
            }
            return ctx.ReadbackOffscreenRgba();
        });
    }

    // The CPU's disc on black: the reference's coverage times its colour clamped, as the GPU's blend over black stores it.
    private static byte[] RenderCpu(in PlanetView view, PlanetColourMap? map, RGBAColor32 flat, float radius, RGBAColor32? night)
    {
        var colours = map ?? new PlanetColourMap(Plane(flat.Red), Plane(flat.Green), Plane(flat.Blue));
        // The CPU's pixel x has its centre AT x; the screen's at x + 0.5.
        var placement = new DiskPlacement(CentreX - 0.5, CentreY - 0.5, radius, view.PoleScreenAngleDeg);
        var rings = view.Body == CatalogIndex.Saturn ? SaturnRings.Main : null;
        var linear = PlanetaryRender.RenderRgba(colours, view.ToAspect(default), placement, Width, Height, PlanetDiscSprite.MinnaertK(view.Body),
            PlanetDiscSprite.Supersample, rings);
        var frame = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            var a = linear[(i * 4) + 3];
            frame[(i * 4) + 3] = 255;
            if (a <= 0)
            {
                continue;
            }
            var (r, g, b) = (linear[i * 4] / a, linear[(i * 4) + 1] / a, linear[(i * 4) + 2] / a);
            if (night is { } tint)
            {
                var luminance = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                (r, g, b) = (luminance * tint.Red / 255f, luminance * tint.Green / 255f, luminance * tint.Blue / 255f);
            }
            frame[i * 4] = Byte(Math.Clamp(r, 0, 1) * a);
            frame[(i * 4) + 1] = Byte(Math.Clamp(g, 0, 1) * a);
            frame[(i * 4) + 2] = Byte(Math.Clamp(b, 0, 1) * a);
        }
        return frame;

        static PlanetMap Plane(byte value)
        {
            var values = new float[4 * 2];
            Array.Fill(values, value / 255f);
            return new PlanetMap(values, 4, 2);
        }
    }

    private static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);

    private static (int Worst, double Mean, int Over) Compare(byte[] a, byte[] b)
    {
        var (worst, over) = (0, 0);
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (i % 4 == 3)
            {
                continue;
            }
            var d = Math.Abs(a[i] - b[i]);
            worst = Math.Max(worst, d);
            sum += d;
            over += d > 4 ? 1 : 0;
        }
        return (worst, sum / (a.Length * 3 / 4), over);
    }

    private async Task WriteAsync(string name, byte[] gpuFrame, byte[] cpuFrame, System.Threading.CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(PlanetBodyGpuTests));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}-gpu.png"), PngWriter.Encode(gpuFrame, Width, Height), ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}-cpu.png"), PngWriter.Encode(cpuFrame, Width, Height), ct);
        output.WriteLine(folder);
    }
}
