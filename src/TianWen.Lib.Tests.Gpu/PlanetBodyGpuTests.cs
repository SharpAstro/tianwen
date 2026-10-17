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
/// Mars gibbous, the Earth from its day map and the Sun. Both draw the disc onto black at one place and size; the GPU's frame is compared with the CPU's coverage
/// times its clamped colour, byte for byte.
/// </summary>
[Collection(GpuCollection.Name)]
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
        // The Earth from its day map (#1573): over Africa and Europe (central meridian 350 W, so 10 E), the Sun to the west.
        { "earth-africa", CatalogIndex.Earth, 23, 350, 10, 35, 0, 110 },
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
        // Every body has its map, the Sun's included since #1608: a comparison of two flat discs would test no texture.
        var map = Maps.GetValueOrDefault(body).ShouldNotBeNull($"{body} has no map in the atlas: the comparison would be of two flat discs");
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
        if (body == CatalogIndex.Sol)
        {
            SunLimbRatio(gpuFrame, radius, 1).ShouldBe(SunLaw(0.98), 0.03, "the GPU's Sun is not limb-darkened by the law");
        }
    }

    // The Sun's green level at 0.98 of the radius along the centre row, both sides averaged, over its level at the centre, of the frame's
    // channel `channel`: the shader's pixel centre at its index plus a half.
    private static double SunLimbRatio(byte[] frame, float radius, int channel)
    {
        var row = (int)CentreY;
        double At(double dx)
        {
            var x = (int)Math.Floor(CentreX + dx);
            return frame[(((row * Width) + x) * 4) + channel];
        }
        var reach = 0.98 * radius;
        return (At(reach) + At(-reach)) / 2 / At(0);
    }

    // The linear law at r of the radius, against the centre.
    private static double SunLaw(double r) => 1 - (PlanetaryRender.SolarLimbDarkening * (1 - Math.Sqrt(1 - (r * r))));

    /// <summary>
    /// Night draws the luminance in the palette's tint: with a tint of no blue, no pixel of a ringed Saturn has any, nor of the Sun (#1608),
    /// whose limb still darkens by the law in the tint's red.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Saturn)]
    [InlineData(CatalogIndex.Sol)]
    public async Task NightHasNoBlue(CatalogIndex body)
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var view = body == CatalogIndex.Sol ? View(body, 7, 0, 0, 0, 0) : View(CatalogIndex.Saturn, 26.7, 40, 25.0, 6, 0);
        var tint = new RGBAColor32(0xE0, 0x30, 0x00, 0xFF);
        const float Radius = 60;
        var frame = RenderGpu(view, Maps[body], default, tint, Radius);
        var cpu = RenderCpu(view, Maps[body], default, Radius, tint);
        var (lit, blue) = (0, 0);
        for (var i = 0; i < frame.Length; i += 4)
        {
            lit += frame[i] > 20 ? 1 : 0;
            blue += frame[i + 2] != 0 ? 1 : 0;
        }
        var (_, mean, _) = Compare(frame, cpu);
        output.WriteLine($"Night: {body}, {lit} lit px, {blue} with any blue, mean difference from the CPU's {mean:F3}");
        await WriteAsync($"{body.ToString().ToLowerInvariant()}-night", frame, cpu, TestContext.Current.CancellationToken);
        lit.ShouldBeGreaterThan(1000);
        blue.ShouldBe(0);
        mean.ShouldBeLessThan(0.02);
        if (body == CatalogIndex.Sol)
        {
            SunLimbRatio(frame, Radius, 0).ShouldBe(SunLaw(0.98), 0.04, "the Night Sun is not limb-darkened by the law");
        }
    }

    /// <summary>
    /// The face of the rings the Sun does not light glows with the light the rings pass through them (<see cref="SaturnRing.UnlitShare"/>):
    /// seen from 25 degrees north with the Sun 8 degrees south, as the orrery's camera sees Saturn from just above the ecliptic in 2026,
    /// the nearly clear C ring and Cassini division glow at almost their lit level and the opaque B ring stays dark, on the CPU's
    /// reference and on the GPU alike. Read off the rings outside the globe's silhouette and its shadow, by each ring's median.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheDarkFaceGlowsThroughTheThinRings()
    {
        const float Radius = 55;
        var view = View(CatalogIndex.Saturn, 25, 300, -8, 30, 0);
        var map = Maps[CatalogIndex.Saturn];
        var cpu = RenderCpu(view, map, default, Radius, null);
        var frames = gpu.VulkanAvailable ? new[] { ("CPU", cpu), ("GPU", RenderGpu(view, map, default, null, Radius)) } : [("CPU", cpu)];
        if (!gpu.VulkanAvailable)
        {
            output.WriteLine($"Vulkan runtime not available on this host ({gpu.UnavailableReason}): the CPU's reference alone");
        }
        foreach (var (host, frame) in frames)
        {
            var c = RingMedian(frame, view, Radius, 74658, 92000);
            var b = RingMedian(frame, view, Radius, 92000, 117580);
            var cassini = RingMedian(frame, view, Radius, 117580, 122170);
            var a = RingMedian(frame, view, Radius, 122170, 136775);
            output.WriteLine($"{host}: the dark face's median level, of 255: C {c}, B {b}, Cassini division {cassini}, A {a}");
            c.ShouldBeGreaterThanOrEqualTo(6, $"{host}: the C ring does not glow");
            cassini.ShouldBeGreaterThanOrEqualTo(6, $"{host}: the Cassini division does not glow");
            // Measured on an Adreno X1-85 and the CPU alike: C 21, B 3, Cassini division 17, A 60; before the unlit face glowed, all 0.
            b.ShouldBeLessThanOrEqualTo(4, $"{host}: the B ring glows");
            b.ShouldBeLessThan(c / 3, $"{host}: the B ring is not darker than the C ring");
        }
        if (frames.Length == 2)
        {
            var (worst, mean, over) = Compare(frames[1].Item2, cpu);
            output.WriteLine($"GPU against CPU: worst {worst}, mean {mean:F3}, {over} values more than 4 off");
            mean.ShouldBeLessThan(0.02);
            over.ShouldBeLessThanOrEqualTo(12);
            await WriteAsync("saturn-dark-face-glow", frames[1].Item2, cpu, TestContext.Current.CancellationToken);
        }
    }

    // The median grey level of the pixels whose ray crosses the ring plane between innerKm and outerKm (200 km inside each edge), off the
    // globe's silhouette and out of its shadow: the shader's own geometry, the pixel's centre at its index plus a half.
    private static int RingMedian(byte[] frame, in PlanetView view, float radius, double innerKm, double outerKm)
    {
        var (right, toward, up, sun) = (view.Right, view.Toward, view.Up, view.Sun);
        var q = 1 - view.Flattening;
        var levels = new System.Collections.Generic.List<int>();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (dx, dy) = ((x + 0.5 - CentreX) / radius, (y + 0.5 - CentreY) / radius);
                if ((dx * dx) + (dy * dy) < 1.05 * 1.05)
                {
                    continue;
                }
                var origin = ((float)dx * right) - ((float)dy * up) + (3 * toward);
                var t = origin.Z / toward.Z;
                var crossing = new Vector2(origin.X - (t * toward.X), origin.Y - (t * toward.Y));
                var km = crossing.Length() * 60268.0;
                if (km < innerKm + 200 || km > outerKm - 200)
                {
                    continue;
                }
                // The globe's shadow: the ray from the crossing toward the Sun meets the spheroid (PlanetaryProjection.InGlobeShadow).
                var qa = (sun.X * sun.X) + (sun.Y * sun.Y) + (sun.Z * sun.Z / (q * q));
                var qb = 2 * ((crossing.X * sun.X) + (crossing.Y * sun.Y));
                var qc = (crossing.X * crossing.X) + (crossing.Y * crossing.Y) - 1;
                var d = (qb * qb) - (4 * qa * qc);
                if (d >= 0 && -qb + Math.Sqrt(d) > 0)
                {
                    continue;
                }
                var i = ((y * Width) + x) * 4;
                levels.Add((frame[i] + frame[i + 1] + frame[i + 2]) / 3);
            }
        }
        levels.Count.ShouldBeGreaterThan(100, $"too few pixels read on the ring from {innerKm} to {outerKm} km");
        levels.Sort();
        return levels[levels.Count / 2];
    }

    /// <summary>
    /// A body is clipped as everything else in its pane is: the orrery draws inside <c>PushClip(contentRect)</c>, and a body straddling
    /// the pane's edge, then a 2D fill drawn after it under the same clip (the selection ring and the labels follow the bodies), leave no
    /// pixel outside the clip. The bodies used to set the scissor to the whole renderer, which the renderer re-applies only when its clip
    /// changes, so they and everything after them in the pane drew unclipped (PR #1589, review round 1).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void ABodyAndWhatFollowsItStayInsideThePanesClip()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var view = View(CatalogIndex.Saturn, 26.7, 40, 25.0, 6, 0);
        var clip = new RectInt(new PointInt(300, 400), new PointInt(100, 100));
        var frame = gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx.ShouldNotBeNull();
            var renderer = gpu.Renderer.ShouldNotBeNull();
            using var bodies = new VkPlanetBodies(ctx, () => { });
            for (var pass = 0; pass < 2; pass++)
            {
                renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
                renderer.PushClip(clip);
                // Saturn centred on the clip's right edge, its rings reaching 136 px either side.
                bodies.Draw(renderer, view, 300, 250, 60, Maps[CatalogIndex.Saturn], 1, default, null).ShouldBe(pass == 1);
                // A strip across the clip's bottom edge, clear of the body: its lower half lies outside the clip.
                renderer.FillRectangle(new RectInt(new PointInt(280, 420), new PointInt(120, 380)), new RGBAColor32(0, 0, 0xFF, 0xFF));
                renderer.PopClip();
                renderer.EndOffscreenFrame();
            }
            return ctx.ReadbackOffscreenRgba();
        });

        var (inside, outside, fillOutside) = (0, 0, 0);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                var within = x >= 100 && x < 300 && y >= 100 && y < 400;
                if (!within && frame[i + 2] > 200 && frame[i] < 20)
                {
                    fillOutside++;
                }
                else if (frame[i] + frame[i + 1] + frame[i + 2] > 0)
                {
                    if (within)
                    {
                        inside++;
                    }
                    else
                    {
                        outside++;
                    }
                }
            }
        }
        output.WriteLine($"lit pixels inside the clip {inside}, the body's outside it {outside}, the later fill's outside it {fillOutside}");
        inside.ShouldBeGreaterThan(1000, "nothing was drawn inside the clip");
        outside.ShouldBe(0, "the body drew outside its pane's clip");
        fillOutside.ShouldBe(0, "a draw after the body drew outside its pane's clip");
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
