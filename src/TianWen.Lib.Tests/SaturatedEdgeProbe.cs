using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: a master's own saturated stars beside the injector's, cut in the same linear unit range and measured with
/// the one <see cref="InjectionMeasure.SaturatedShape"/>, so the soft edge R1's first checks found missing can be looked
/// at (docs/plans/star-remover-training.md, "The first checks' reading"). The injected stars are rendered exactly as the
/// Stars mode renders them (<see cref="InjectionPopulation"/> drawn with every star saturated, <see cref="StarInjection.Render"/>
/// onto the plate), without the stars' own shot noise, which a saturated core does not show. Writes, per session,
/// <c>&lt;slug&gt;.real.f32</c> and <c>&lt;slug&gt;.inj.f32</c> (raw little-endian float32, n windows of 81 x 81, the
/// luminance) and <c>&lt;slug&gt;.csv</c> (each window's centre, plateau and edge).
/// <para>Set <c>TIANWEN_SATEDGE_BAKE</c> (the bake root), <c>TIANWEN_SATEDGE_PLATES</c> (a starless-plates store),
/// <c>TIANWEN_SATEDGE_OUT</c> and <c>TIANWEN_SATEDGE_SESSIONS</c> (semicolon-separated session ids or prefixes of them).</para>
/// </summary>
[Collection("Imaging")]
public sealed class SaturatedEdgeProbe(ITestOutputHelper output)
{
    private const int Half = InjectionMeasure.SaturatedWindowPx;
    private const int Side = 2 * Half + 1;
    private const int PerKind = 16;
    private const int RegionSize = 512;

    [Fact]
    public async Task CutRealAndInjectedSaturatedStarsSideBySide()
    {
        var bake = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_BAKE");
        var platesRoot = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_PLATES");
        var outDir = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_OUT");
        var sessions = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_SESSIONS");
        Assert.SkipWhen(bake is null || platesRoot is null || outDir is null || sessions is null, "TIANWEN_SATEDGE_* not set");
        var ct = TestContext.Current.CancellationToken;
        var psfStore = await DatasetPsfStore.ReadAsync(Path.Combine(bake, "stats", DatasetPsfStore.FileName), null, ct);
        var context = new DatasetDegradationExporter.StarsContext(Path.Combine(platesRoot, "plates"), psfStore);
        Directory.CreateDirectory(outDir);

        foreach (var prefix in sessions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The store keeps rows of ids an older bake used; the session is the one with a retained master.
            var sessionId = psfStore.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && File.Exists(RetainedMasterStore.PathFor(bake, k)))
                .Order(StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException($"no session with a retained master in the PSF store starts with {prefix}");
            Assert.True(RetainedMasterStore.TryRead(bake, sessionId, out var master), $"{sessionId}: no retained master");
            Assert.True(Image.TryReadFitsFile(context.PlatePath(sessionId), out var plate), $"{sessionId}: no plate");
            var catalogue = await StarlessCatalogue.ReadAsync(context.CataloguePath(sessionId), ct);
            var (channels, width, height) = master.Shape;
            var psf = context.ChannelPsf(sessionId, channels, out _);
            var divisor = DatasetTileExporter.UnitDivisor(master);
            var absent = plate.AbsentPixels();
            var population = InjectionPopulation.Build(catalogue, master, plate, 1.0 / divisor, psf, absent);
            var csv = new StringBuilder("kind,i,x,y,plateau_px,edge_px\n");

            // The master's own, at random among those whose window fits.
            var lum = DatasetDegradationExporter.Luminance(master);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] /= divisor;
            }
            var rng = new Random(1);
            var candidates = catalogue
                .Where(static s => s.Outcome == StarFitOutcome.Subtracted && s.Saturated)
                .Where(s => InjectionMeasure.SaturatedShape(lum, width, height, s.X, s.Y) is not null)
                .ToList();
            var real = new List<float[]>();
            while (candidates.Count > 0 && real.Count < PerKind)
            {
                var k = rng.Next(candidates.Count);
                var s = candidates[k];
                candidates.RemoveAt(k);
                var shape = InjectionMeasure.SaturatedShape(lum, width, height, s.X, s.Y);
                real.Add(Cut(lum, width, s.X, s.Y));
                csv.Append(CultureInfo.InvariantCulture, $"real,{real.Count - 1},{s.X:F2},{s.Y:F2},{shape?.PlateauPx},{shape?.EdgePx:F2}\n");
            }

            // The injector's, every star of a draw saturated as the saturated arm draws them, onto the plate's own pixels.
            var unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
            var injected = new List<float[]>();
            for (var draw = 0; draw < 4000 && injected.Count < PerKind; draw++)
            {
                var cx = rng.Next(0, width - RegionSize);
                var cy = rng.Next(0, height - RegionSize);
                if (absent is { } a && a[cy + RegionSize / 2, cx + RegionSize / 2])
                {
                    continue;
                }
                var plan = population.Plan(cx, cy, RegionSize, DatasetDegradationExporter.InjectionMarginPx, InjectionPlacement.Random,
                    StarProfileFamily.Moffat, 1.0, new Random(draw));
                var local = plan.Stars.Select(s => s with { X = s.X - cx, Y = s.Y - cy }).ToArray();
                if (!local.Any(static s => s.Saturated))
                {
                    continue;
                }
                var basePlanes = new float[channels][];
                for (var c = 0; c < channels; c++)
                {
                    var plane = unitPlate.GetChannelSpan(c);
                    basePlanes[c] = new float[RegionSize * RegionSize];
                    for (var y = 0; y < RegionSize; y++)
                    {
                        plane.Slice(((cy + y) * width) + cx, RegionSize).CopyTo(basePlanes[c].AsSpan(y * RegionSize, RegionSize));
                    }
                }
                BitMatrix? regionAbsent = null;
                if (absent is { } frameAbsent)
                {
                    var mask = new BitMatrix(RegionSize, RegionSize);
                    for (var y = 0; y < RegionSize; y++)
                    {
                        for (var x = 0; x < RegionSize; x++)
                        {
                            mask[y, x] = frameAbsent[cy + y, cx + x];
                        }
                    }
                    regionAbsent = mask;
                }
                var floors = Enumerable.Repeat(1e-7, channels).ToArray();
                var render = StarInjection.Render(basePlanes, RegionSize, RegionSize, regionAbsent, local, floors, new Random(draw ^ 0x6d2b79f5));
                var renderLum = new float[RegionSize * RegionSize];
                for (var c = 0; c < channels; c++)
                {
                    for (var i = 0; i < renderLum.Length; i++)
                    {
                        renderLum[i] += render.Planes[c][i] / channels;
                    }
                }
                foreach (var s in local.Where(static s => s.Saturated))
                {
                    if (injected.Count < PerKind && InjectionMeasure.SaturatedShape(renderLum, RegionSize, RegionSize, s.X, s.Y) is { } shape)
                    {
                        injected.Add(Cut(renderLum, RegionSize, s.X, s.Y));
                        csv.Append(CultureInfo.InvariantCulture, $"inj,{injected.Count - 1},{s.X + cx:F2},{s.Y + cy:F2},{shape.PlateauPx},{shape.EdgePx:F2}\n");
                    }
                }
            }

            var slug = DatasetTileExporter.Sanitize(sessionId);
            await WriteAsync(Path.Combine(outDir, slug + ".real.f32"), real);
            await WriteAsync(Path.Combine(outDir, slug + ".inj.f32"), injected);
            await File.WriteAllTextAsync(Path.Combine(outDir, slug + ".csv"), csv.ToString(), ct);
            output.WriteLine($"{slug}: {real.Count} real, {injected.Count} injected (of {population.SaturatedPool} in the saturated pool)");
            unitPlate.Release();
            plate.Release();
            master.Release();
        }
    }

    // The window about the star's pixel, row-major, Side x Side (the measure's own window).
    private static float[] Cut(float[] plane, int width, double cx, double cy)
    {
        var window = new float[Side * Side];
        var x0 = (int)cx - Half;
        var y0 = (int)cy - Half;
        for (var y = 0; y < Side; y++)
        {
            plane.AsSpan(((y0 + y) * width) + x0, Side).CopyTo(window.AsSpan(y * Side, Side));
        }
        return window;
    }

    private static async Task WriteAsync(string path, List<float[]> windows)
    {
        var bytes = new byte[windows.Count * Side * Side * sizeof(float)];
        for (var k = 0; k < windows.Count; k++)
        {
            Buffer.BlockCopy(windows[k], 0, bytes, k * Side * Side * sizeof(float), Side * Side * sizeof(float));
        }
        await File.WriteAllBytesAsync(path, bytes);
    }
}
