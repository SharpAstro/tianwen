using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;
using TianWen.Lib.Stat;
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
            var csv = new StringBuilder("kind,i,x,y,plateau_px,edge_px,overshoot\n");

            // The master's own, at random among those whose window fits.
            var lum = DatasetDegradationExporter.Luminance(master);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] /= divisor;
            }
            var rng = new Random(1);
            var candidates = catalogue
                .Where(s => InjectionPopulation.InSaturatedPool(s, channels))
                .Where(s => InjectionMeasure.SaturatedShape(lum, width, height, s.X, s.Y) is not null)
                .ToList();
            var real = new List<float[]>();
            var chosen = new List<FittedStar>();
            while (candidates.Count > 0 && real.Count < PerKind)
            {
                var k = rng.Next(candidates.Count);
                var s = candidates[k];
                candidates.RemoveAt(k);
                var shape = InjectionMeasure.SaturatedShape(lum, width, height, s.X, s.Y);
                real.Add(Cut(lum, width, s.X, s.Y));
                chosen.Add(s);
                csv.Append(CultureInfo.InvariantCulture, $"real,{real.Count - 1},{s.X:F2},{s.Y:F2},{shape?.PlateauPx},{shape?.EdgePx:F2},\n");
            }

            // The plate in the unit range, a region of it with the given stars rendered as the Stars mode renders them, and
            // the luminance of the result; and a star's overshoot: its brightest channel's amplitude over that channel's
            // clip above the plate beneath it (how far past the clip the stack's subs were driven).
            var unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
            var unitPlanes = Enumerable.Range(0, channels).Select(c => unitPlate.GetChannelSpan(c).ToArray()).ToArray();
            float[] Rendered(int cx, int cy, int size, InjectedStar[] local, int seed)
                => RenderLuminance(unitPlanes, width, absent, cx, cy, size, local, seed);
            double Overshoot(InjectedStar s, double frameX, double frameY)
            {
                var px = Math.Clamp((int)Math.Round(frameX), 0, width - 1);
                var py = Math.Clamp((int)Math.Round(frameY), 0, height - 1);
                var best = double.NaN;
                for (var c = 0; c < channels; c++)
                {
                    var room = s.ClipLevels[c] - unitPlate.GetChannelSpan(c)[(py * width) + px];
                    if (double.IsFinite(room) && room > 0 && !(s.Amplitudes[c] / room <= best))
                    {
                        best = s.Amplitudes[c] / room;
                    }
                }
                return best;
            }

            // The injector's, every star of a draw saturated as the saturated arm draws them, onto the plate's own pixels.
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
                var renderLum = Rendered(cx, cy, RegionSize, local, draw);
                foreach (var s in local.Where(static s => s.Saturated))
                {
                    if (injected.Count < PerKind && InjectionMeasure.SaturatedShape(renderLum, RegionSize, RegionSize, s.X, s.Y) is { } shape)
                    {
                        injected.Add(Cut(renderLum, RegionSize, s.X, s.Y));
                        csv.Append(CultureInfo.InvariantCulture,
                            $"inj,{injected.Count - 1},{s.X + cx:F2},{s.Y + cy:F2},{shape.PlateauPx},{shape.EdgePx:F2},{Overshoot(s, s.X + cx, s.Y + cy):F2}\n");
                    }
                }
            }

            // Each real star again, rendered by the injector AT ITS OWN SITE on the plate with its own pool entry (its
            // catalogue amplitudes, its clip read off the master) and the profile the injector would give it there: where
            // this matches the real star but the injected draws do not, the miss is in what is drawn; where it does not
            // match, the render's top is.
            var masterPlanes = new float[channels][];
            var platePlanes = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                masterPlanes[c] = master.GetChannelSpan(c).ToArray();
                platePlanes[c] = plate.GetChannelSpan(c).ToArray();
            }
            var site = new List<float[]>();
            const int SiteSize = (2 * Half) + 17;
            foreach (var (s, k) in chosen.Select(static (s, k) => (s, k)))
            {
                var ox = (int)s.X - (SiteSize / 2);
                var oy = (int)s.Y - (SiteSize / 2);
                if (ox < 0 || oy < 0 || ox + SiteSize > width || oy + SiteSize > height)
                {
                    continue;
                }
                var (amplitudes, clips) = InjectionPopulation.SaturatedEntry(s, masterPlanes, platePlanes, width, height, 1.0 / divisor);
                var star = new InjectedStar(s.X - ox, s.Y - oy, amplitudes, population.ProfilesAt(s.X, s.Y, StarProfileFamily.Moffat), Saturated: true, clips);
                var renderLum = Rendered(ox, oy, SiteSize, [star], 1000 + k);
                if (InjectionMeasure.SaturatedShape(renderLum, SiteSize, SiteSize, star.X, star.Y) is { } shape)
                {
                    site.Add(Cut(renderLum, SiteSize, star.X, star.Y));
                    csv.Append(CultureInfo.InvariantCulture,
                        $"site,{k},{s.X:F2},{s.Y:F2},{shape.PlateauPx},{shape.EdgePx:F2},{Overshoot(star, s.X, s.Y):F2}\n");
                }
            }

            var slug = DatasetTileExporter.Sanitize(sessionId);
            await WriteAsync(Path.Combine(outDir, slug + ".real.f32"), real);
            await WriteAsync(Path.Combine(outDir, slug + ".inj.f32"), injected);
            await WriteAsync(Path.Combine(outDir, slug + ".site.f32"), site);
            await File.WriteAllTextAsync(Path.Combine(outDir, slug + ".csv"), csv.ToString(), ct);
            output.WriteLine($"{slug}: {real.Count} real, {injected.Count} injected (of {population.SaturatedPool} in the saturated pool)");
            unitPlate.Release();
            plate.Release();
            master.Release();
        }
    }

    // Radial bins (pixels) the profiles below are read in.
    private static readonly double[] RadialEdges = [0, 0.75, 1.25, 1.75, 2.25, 2.75, 3.5, 4.5, 6, 8, 11, 15, 20];

    /// <summary>
    /// Env-gated, the same variables as above: whether a saturated star is drawn too bright for the profile it is drawn
    /// with. Each pool star's amplitude is read again, per channel, off its own near-core ring (pixels between 0.1 and 0.6 of
    /// its clip above a sky taken from the plate beyond the star) with the profile the injector draws it with, set against
    /// the catalogue's (R0's, fitted on the wings with R0's own profile), and the star re-rendered at its own site with each,
    /// beside the real one: plateau, edge, the first ring over the maximum, and the radial profile out to 20 px. Bright
    /// unsaturated stars give the same radial profile against the injector's profile fitted on their core, which says
    /// whether the profile has the wings the real stars have. Writes <c>&lt;slug&gt;.amp.csv</c> per session.
    /// </summary>
    [Fact]
    public async Task ReadEachSaturatedStarsAmplitudeOffItsOwnNearCoreRing()
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
        var bins = RadialEdges.Length - 1;

        foreach (var prefix in sessions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
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
            var field = await StarlessFieldProfile.ReadAsync(context.ProfilePath(sessionId), ct);
            // The session's seeing from sub to sub, the stacker's own per-sub FWHM: a robust log scatter.
            var subFwhm = psfStore[sessionId].SubFwhm.Where(static f => f > 0).Select(static f => Math.Log(f)).ToArray();
            var seeingLogSd = subFwhm.Length > 2
                ? 1.4826 * StatisticsHelper.NthSmallest(subFwhm.Select(f => Math.Abs(f - StatisticsHelper.NthSmallest(subFwhm.ToArray(), subFwhm.Length / 2))).ToArray(), subFwhm.Length / 2)
                : StarInjection.SubScatter;
            var subGridSubs = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_SUBS"), out var n) ? n : 24;
            var population = InjectionPopulation.Build(catalogue, master, plate, 1.0 / divisor, psf, absent, field);
            var masterPlanes = new float[channels][];
            var platePlanes = new float[channels][];
            var unitMaster = new float[channels][];
            var unitPlate = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                masterPlanes[c] = master.GetChannelSpan(c).ToArray();
                platePlanes[c] = plate.GetChannelSpan(c).ToArray();
                unitMaster[c] = masterPlanes[c].Select(v => v / divisor).ToArray();
                unitPlate[c] = platePlanes[c].Select(v => v / divisor).ToArray();
            }
            var lum = Mean(unitMaster);
            var plateLum = Mean(unitPlate);
            // What R0 took out: outside a star's inpainted core the master less the plate is R0's own model of it.
            var subtracted = lum.Select((v, i) => v - plateLum[i]).ToArray();
            var fieldFwhm = psf.Average(static p => p.Fwhm);
            bool Usable(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && !(absent is { } a && a[y, x]);

            // The sky under a star: the plate's median in an annulus past the star's own reach (inside it the plate is the
            // master less R0's model, the very thing being judged).
            double Sky(float[] plane, double cx, double cy)
            {
                var inner = Math.Max(12.0, 6.0 * fieldFwhm);
                var outer = Math.Max(20.0, 10.0 * fieldFwhm);
                var values = new List<float>();
                var r = (int)Math.Ceiling(outer);
                for (var y = (int)cy - r; y <= (int)cy + r; y++)
                {
                    for (var x = (int)cx - r; x <= (int)cx + r; x++)
                    {
                        var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                        if (d2 >= inner * inner && d2 <= outer * outer && Usable(x, y) && float.IsFinite(plane[(y * width) + x]))
                        {
                            values.Add(plane[(y * width) + x]);
                        }
                    }
                }
                return values.Count > 20 ? StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(values), values.Count / 2) : double.NaN;
            }

            // A plane's mean above a level in each radial bin about a centre (NaN where a bin is empty).
            // The median, not the mean: a faint neighbour lifts a few pixels of an annulus, and in a crowded field a mean read
            // every neighbour as the star's own halo.
            double[] Radial(float[] plane, int planeWidth, int ox, int oy, double cx, double cy, double level)
            {
                var values = Enumerable.Range(0, bins).Select(static _ => new List<float>()).ToArray();
                var r = (int)Math.Ceiling(RadialEdges[^1]);
                for (var y = (int)cy - r; y <= (int)cy + r; y++)
                {
                    for (var x = (int)cx - r; x <= (int)cx + r; x++)
                    {
                        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        var b = Array.FindLastIndex(RadialEdges, e => e <= d);
                        var px = x - ox;
                        var py = y - oy;
                        if (b < 0 || b >= bins || px < 0 || py < 0 || px >= planeWidth || py * planeWidth + px >= plane.Length)
                        {
                            continue;
                        }
                        var v = plane[(py * planeWidth) + px];
                        if (float.IsFinite(v))
                        {
                            values[b].Add((float)(v - level));
                        }
                    }
                }
                return values.Select(static list => list.Count > 0 ? StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(list), list.Count / 2) : double.NaN).ToArray();
            }

            // The least-squares amplitude of a profile over the pixels whose light above the sky lies in [lo, hi] of a level.
            (double Amplitude, int Pixels) RingAmplitude(float[] plane, StarProfile profile, double cx, double cy, double sky, double level, double lo, double hi, double reach)
            {
                double sxy = 0, sxx = 0;
                var n = 0;
                var r = (int)Math.Ceiling(reach);
                for (var y = (int)Math.Round(cy) - r; y <= (int)Math.Round(cy) + r; y++)
                {
                    for (var x = (int)Math.Round(cx) - r; x <= (int)Math.Round(cx) + r; x++)
                    {
                        if (!Usable(x, y) || (x - cx) * (x - cx) + (y - cy) * (y - cy) > reach * reach)
                        {
                            continue;
                        }
                        var v = plane[(y * width) + x] - sky;
                        if (!double.IsFinite(v) || v < lo * level || v > hi * level)
                        {
                            continue;
                        }
                        var p = profile.PixelMean(x, y, cx, cy);
                        sxy += v * p;
                        sxx += p * p;
                        n++;
                    }
                }
                return (n >= 6 && sxx > 0 ? sxy / sxx : double.NaN, n);
            }

            // The first ring (0.75 to 1.25 px) over the brightest pixel within 2 px, both above the level.
            static double FirstRing(double[] radial, float[] plane, int planeWidth, int ox, int oy, double cx, double cy, double level)
            {
                var max = double.NegativeInfinity;
                for (var y = (int)cy - 2; y <= (int)cy + 2; y++)
                {
                    for (var x = (int)cx - 2; x <= (int)cx + 2; x++)
                    {
                        var i = ((y - oy) * planeWidth) + x - ox;
                        if (i >= 0 && i < plane.Length && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= 4.0 && plane[i] - level > max)
                        {
                            max = plane[i] - level;
                        }
                    }
                }
                return radial[1] / max;
            }

            var csv = new StringBuilder("kind,i,x,y,significance,overshoot_r0,overshoot_ring");
            for (var c = 0; c < channels; c++)
            {
                csv.Append(CultureInfo.InvariantCulture, $",a_r0_{c},a_ring_{c},n_ring_{c},room_{c}");
            }
            csv.Append(",plateau_real,plateau_r0,plateau_ring,edge_real,edge_r0,edge_ring,ring1_real,ring1_r0,ring1_ring,plateau_field,edge_field,ring1_field,far,plateau_stackfar,ring1_stackfar,plateau_subgrid,ring1_subgrid");
            foreach (var kind in new[] { "real", "r0", "ring", "sub", "field", "stackfar", "subgrid" })
            {
                for (var b = 0; b < bins; b++)
                {
                    csv.Append(CultureInfo.InvariantCulture, $",{kind}_{(RadialEdges[b] + RadialEdges[b + 1]) / 2:F2}");
                }
            }
            csv.Append('\n');

            const int SiteSize = (2 * Half) + 17;
            // In a seeded random order, at most TIANWEN_SATEDGE_MAXSAT of them (the sub-grid render is the expensive part).
            var maxSaturated = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_MAXSAT"), out var cap) ? cap : int.MaxValue;
            var order = new Random(7);
            var pool = catalogue.Select(static (s, k) => (s, k)).Where(t => InjectionPopulation.InSaturatedPool(t.s, channels))
                .OrderBy(_ => order.Next()).ToList();
            var written = 0;
            foreach (var (s, k) in pool)
            {
                var ox = (int)s.X - (SiteSize / 2);
                var oy = (int)s.Y - (SiteSize / 2);
                if (ox < 0 || oy < 0 || ox + SiteSize > width || oy + SiteSize > height || InjectionMeasure.SaturatedShape(lum, width, height, s.X, s.Y) is not { } realShape)
                {
                    continue;
                }
                var (amplitudes, clips) = InjectionPopulation.SaturatedEntry(s, masterPlanes, platePlanes, width, height, 1.0 / divisor);
                var profiles = population.ProfilesAt(s.X, s.Y, StarProfileFamily.Moffat);
                var ring = new double[channels];
                var ringN = new int[channels];
                var rooms = new double[channels];
                for (var c = 0; c < channels; c++)
                {
                    var sky = Sky(unitPlate[c], s.X, s.Y);
                    rooms[c] = clips[c] - sky;
                    (ring[c], ringN[c]) = double.IsFinite(rooms[c]) && rooms[c] > 0
                        ? RingAmplitude(unitMaster[c], profiles[c], s.X, s.Y, sky, rooms[c], 0.1, 0.6, Math.Max(4.0, 4.0 * psf[c].Fwhm))
                        : (double.NaN, 0);
                }
                var ringAmplitudes = Enumerable.Range(0, channels)
                    .Select(c => double.IsFinite(ring[c]) && ring[c] > 0 ? ring[c] : amplitudes[c]).ToImmutableArray();
                var r0Star = new InjectedStar(s.X - ox, s.Y - oy, amplitudes, profiles, Saturated: true, clips);
                var ringStar = r0Star with { Amplitudes = ringAmplitudes };
                var r0Lum = RenderLuminance(unitPlate, width, absent, ox, oy, SiteSize, [r0Star], 1000 + k);
                var ringLum = RenderLuminance(unitPlate, width, absent, ox, oy, SiteSize, [ringStar], 1000 + k);
                var r0Shape = InjectionMeasure.SaturatedShape(r0Lum, SiteSize, SiteSize, r0Star.X, r0Star.Y);
                var ringShape = InjectionMeasure.SaturatedShape(ringLum, SiteSize, SiteSize, ringStar.X, ringStar.Y);
                // The catalogue's own amplitude drawn with the profile it was fitted with, the plate builder's.
                var fieldLum = field is null ? null
                    : RenderLuminance(unitPlate, width, absent, ox, oy, SiteSize, [r0Star with { Profiles = population.ProfilesAt(s.X, s.Y, StarProfileFamily.Field) }], 1000 + k);
                var fieldShape = fieldLum is null ? null : InjectionMeasure.SaturatedShape(fieldLum, SiteSize, SiteSize, r0Star.X, r0Star.Y);

                var skyLum = Sky(plateLum, s.X, s.Y);
                var plateCut = new float[SiteSize * SiteSize];
                for (var y = 0; y < SiteSize; y++)
                {
                    plateLum.AsSpan(((oy + y) * width) + ox, SiteSize).CopyTo(plateCut.AsSpan(y * SiteSize, SiteSize));
                }
                var realRadial = Radial(lum, width, 0, 0, s.X, s.Y, skyLum);
                var subRadial = Radial(subtracted, width, 0, 0, s.X, s.Y, 0.0);
                // A render's star alone is the render less the plate it was rendered on.
                var r0Star2 = r0Lum.Select((v, i) => v - plateCut[i]).ToArray();
                var ringStar2 = ringLum.Select((v, i) => v - plateCut[i]).ToArray();
                var r0Radial = Radial(r0Star2, SiteSize, ox, oy, s.X, s.Y, 0.0);
                var ringRadial = Radial(ringStar2, SiteSize, ox, oy, s.X, s.Y, 0.0);
                var fieldStar2 = fieldLum?.Select((v, i) => v - plateCut[i]).ToArray();
                var fieldRadial = fieldStar2 is null ? Enumerable.Repeat(double.NaN, bins).ToArray() : Radial(fieldStar2, SiteSize, ox, oy, s.X, s.Y, 0.0);
                // The amplitude the star's own far wing (5 to 8 px, unclipped in every sub) implies with the field profile, and
                // the star drawn with it twice: clipped on the stack's grid as the injector clips, and on each sub's own grid.
                var far = (realRadial[7] / fieldRadial[7] + realRadial[8] / fieldRadial[8]) / 2.0;
                var nanRadial = Enumerable.Repeat(double.NaN, bins).ToArray();
                double[] stackFarRadial = nanRadial, subGridRadial = nanRadial;
                double ring1StackFar = double.NaN, ring1SubGrid = double.NaN;
                SaturatedStarShape? stackFarShape = null, subGridShape = null;
                if (field is not null && double.IsFinite(far) && far > 0)
                {
                    var farStar = r0Star with
                    {
                        Amplitudes = [.. amplitudes.Select(a => a * far)],
                        Profiles = population.ProfilesAt(s.X, s.Y, StarProfileFamily.Field),
                    };
                    var stackFarLum = RenderLuminance(unitPlate, width, absent, ox, oy, SiteSize, [farStar], 1000 + k);
                    var subGridLum = RenderOnSubGrids(unitPlate, width, ox, oy, SiteSize, farStar, subGridSubs, seeingLogSd, 0.02, 1000 + k);
                    stackFarShape = InjectionMeasure.SaturatedShape(stackFarLum, SiteSize, SiteSize, farStar.X, farStar.Y);
                    subGridShape = InjectionMeasure.SaturatedShape(subGridLum, SiteSize, SiteSize, farStar.X, farStar.Y);
                    var stackFarStar = stackFarLum.Select((v, i) => v - plateCut[i]).ToArray();
                    var subGridStar = subGridLum.Select((v, i) => v - plateCut[i]).ToArray();
                    stackFarRadial = Radial(stackFarStar, SiteSize, ox, oy, s.X, s.Y, 0.0);
                    subGridRadial = Radial(subGridStar, SiteSize, ox, oy, s.X, s.Y, 0.0);
                    ring1StackFar = FirstRing(stackFarRadial, stackFarStar, SiteSize, ox, oy, s.X, s.Y, 0.0);
                    ring1SubGrid = FirstRing(subGridRadial, subGridStar, SiteSize, ox, oy, s.X, s.Y, 0.0);
                }
                double Overshoot(IReadOnlyList<double> a) => Enumerable.Range(0, channels)
                    .Where(c => double.IsFinite(rooms[c]) && rooms[c] > 0).Select(c => a[c] / rooms[c]).DefaultIfEmpty(double.NaN).Max();

                csv.Append(CultureInfo.InvariantCulture, $"sat,{k},{s.X:F2},{s.Y:F2},{s.Significance:F0},{Overshoot(amplitudes):F3},{Overshoot(ringAmplitudes):F3}");
                for (var c = 0; c < channels; c++)
                {
                    csv.Append(CultureInfo.InvariantCulture, $",{amplitudes[c]:G6},{ring[c]:G6},{ringN[c]},{rooms[c]:G6}");
                }
                csv.Append(CultureInfo.InvariantCulture,
                    $",{realShape.PlateauPx},{r0Shape?.PlateauPx},{ringShape?.PlateauPx},{realShape.EdgePx:F2},{r0Shape?.EdgePx:F2},{ringShape?.EdgePx:F2}");
                csv.Append(CultureInfo.InvariantCulture,
                    $",{FirstRing(realRadial, lum, width, 0, 0, s.X, s.Y, skyLum):F4},{FirstRing(r0Radial, r0Star2, SiteSize, ox, oy, s.X, s.Y, 0.0):F4},{FirstRing(ringRadial, ringStar2, SiteSize, ox, oy, s.X, s.Y, 0.0):F4}");
                csv.Append(CultureInfo.InvariantCulture,
                    $",{fieldShape?.PlateauPx},{fieldShape?.EdgePx:F2},{(fieldStar2 is null ? double.NaN : FirstRing(fieldRadial, fieldStar2, SiteSize, ox, oy, s.X, s.Y, 0.0)):F4}");
                csv.Append(CultureInfo.InvariantCulture,
                    $",{far:F4},{stackFarShape?.PlateauPx},{ring1StackFar:F4},{subGridShape?.PlateauPx},{ring1SubGrid:F4}");
                foreach (var radial in new[] { realRadial, r0Radial, ringRadial, subRadial, fieldRadial, stackFarRadial, subGridRadial })
                {
                    foreach (var v in radial)
                    {
                        csv.Append(CultureInfo.InvariantCulture, $",{v:G6}");
                    }
                }
                csv.Append('\n');
                written++;
                if (written >= maxSaturated)
                {
                    break;
                }
            }

            // Bright unsaturated stars: the injector's profile fitted on the core (the pixels above 0.3 of the peak), and the
            // radial profile against it, the model rendered unclipped in place of a re-render.
            var cell = Math.Max(8.0, 4.0 * fieldFwhm);
            var occupied = catalogue.GroupBy(s => ((int)(s.X / cell), (int)(s.Y / cell))).ToDictionary(static g => g.Key, static g => g.Count());
            bool Isolated(FittedStar s)
            {
                var (cx, cy) = ((int)(s.X / cell), (int)(s.Y / cell));
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        n += occupied.GetValueOrDefault((cx + dx, cy + dy));
                    }
                }
                return n == 1;
            }
            var brightWritten = 0;
            foreach (var (s, k) in catalogue.Select(static (s, k) => (s, k))
                .Where(t => !t.s.Saturated && t.s.Outcome == StarFitOutcome.Subtracted && t.s.Significance is >= 300f and <= 5000f)
                .Where(t => t.s.X > 25 && t.s.Y > 25 && t.s.X < width - 26 && t.s.Y < height - 26 && Isolated(t.s)).Take(300))
            {
                var profiles = population.ProfilesAt(s.X, s.Y, StarProfileFamily.Moffat);
                var skyLum = Sky(plateLum, s.X, s.Y);
                var realRadial = Radial(lum, width, 0, 0, s.X, s.Y, skyLum);
                var subRadial = Radial(subtracted, width, 0, 0, s.X, s.Y, 0.0);
                var model = new float[41 * 41];
                var ox = (int)s.X - 20;
                var oy = (int)s.Y - 20;
                for (var c = 0; c < channels; c++)
                {
                    var sky = Sky(unitPlate[c], s.X, s.Y);
                    var peak = RingAmplitude(unitMaster[c], profiles[c], s.X, s.Y, sky, 1.0, double.NegativeInfinity, double.PositiveInfinity, 1.0);
                    var (a, _) = RingAmplitude(unitMaster[c], profiles[c], s.X, s.Y, sky, Math.Max(peak.Amplitude, 1e-12), 0.3, 2.0, Math.Max(3.0, 2.0 * psf[c].Fwhm));
                    for (var y = 0; y < 41; y++)
                    {
                        for (var x = 0; x < 41; x++)
                        {
                            model[(y * 41) + x] += (float)(a * profiles[c].PixelMean(ox + x, oy + y, s.X, s.Y) / channels);
                        }
                    }
                }
                var modelRadial = Radial(model, 41, ox, oy, s.X, s.Y, 0.0);
                var fieldRadial = Enumerable.Repeat(double.NaN, bins).ToArray();
                if (field is not null)
                {
                    var fieldProfiles = population.ProfilesAt(s.X, s.Y, StarProfileFamily.Field);
                    var fieldModel = new float[41 * 41];
                    for (var c = 0; c < channels; c++)
                    {
                        var sky = Sky(unitPlate[c], s.X, s.Y);
                        var peak = RingAmplitude(unitMaster[c], fieldProfiles[c], s.X, s.Y, sky, 1.0, double.NegativeInfinity, double.PositiveInfinity, 1.0);
                        var (a, _) = RingAmplitude(unitMaster[c], fieldProfiles[c], s.X, s.Y, sky, Math.Max(peak.Amplitude, 1e-12), 0.3, 2.0, Math.Max(3.0, 2.0 * psf[c].Fwhm));
                        for (var y = 0; y < 41; y++)
                        {
                            for (var x = 0; x < 41; x++)
                            {
                                fieldModel[(y * 41) + x] += (float)(a * fieldProfiles[c].PixelMean(ox + x, oy + y, s.X, s.Y) / channels);
                            }
                        }
                    }
                    fieldRadial = Radial(fieldModel, 41, ox, oy, s.X, s.Y, 0.0);
                }
                csv.Append(CultureInfo.InvariantCulture, $"bright,{k},{s.X:F2},{s.Y:F2},{s.Significance:F0},,");
                for (var c = 0; c < channels; c++)
                {
                    csv.Append(",,,,");
                }
                csv.Append(",,,,,,,,,,,,,,,,,");
                foreach (var radial in new[] { realRadial, modelRadial, modelRadial, subRadial, fieldRadial, fieldRadial, fieldRadial })
                {
                    foreach (var v in radial)
                    {
                        csv.Append(CultureInfo.InvariantCulture, $",{v:G6}");
                    }
                }
                csv.Append('\n');
                brightWritten++;
            }

            var slug = DatasetTileExporter.Sanitize(sessionId);
            await File.WriteAllTextAsync(Path.Combine(outDir, slug + ".amp.csv"), csv.ToString(), ct);
            output.WriteLine($"{slug}: {written} saturated of {pool.Count} in the pool, {brightWritten} bright unsaturated");
            plate.Release();
            master.Release();
        }
    }

    /// <summary>
    /// A saturated star stacked as the subs saw it: each of <paramref name="subs"/> virtual subs on its OWN pixel grid, the star
    /// landing at a uniformly random sub-pixel phase there (a dither puts it anywhere on the grid), its width scattered by the
    /// session's measured seeing (<paramref name="widthLogSd"/>) and its amplitude by <paramref name="amplitudeLogSd"/>, the plate
    /// plus star clipped on that grid, then warped back onto the stack's grid with the bake's own kernel (clamped Lanczos-3) and
    /// averaged. What <see cref="StarInjection"/> leaves out: it clips each virtual sub on the stack's grid, where the core pixel
    /// is the same in every sub, while a raw sub's peak pixel moves with the phase (14 percent from sub to sub on eta Carinae).
    /// </summary>
    private static float[] RenderOnSubGrids(
        float[][] unitPlate, int width, int ox, int oy, int size, InjectedStar star, int subs, double widthLogSd, double amplitudeLogSd, int seed)
    {
        var channels = unitPlate.Length;
        var rng = new Random(seed);
        var sum = new double[channels][];
        var raw = new float[size * size];
        for (var c = 0; c < channels; c++)
        {
            sum[c] = new double[size * size];
        }
        for (var k = 0; k < subs; k++)
        {
            var u = rng.NextDouble() - 0.5;
            var v = rng.NextDouble() - 0.5;
            var widthScale = Math.Exp(widthLogSd * StarInjection.Gaussian(rng));
            var amplitudeScale = Math.Exp(amplitudeLogSd * StarInjection.Gaussian(rng));
            for (var c = 0; c < channels; c++)
            {
                var profile = star.Profiles[c].Scaled(widthScale);
                var clip = star.ClipLevels[c];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        // The raw sub's pixel (x, y): the plate (smooth, so read at the stack's pixel) plus the star centred at
                        // its phase on this grid, clipped where the sub clipped.
                        var plate = unitPlate[c][((oy + y) * width) + ox + x];
                        var total = plate + (star.Amplitudes[c] * amplitudeScale * profile.PixelMean(x, y, star.X + u, star.Y + v));
                        raw[(y * size) + x] = (float)(double.IsFinite(clip) ? Math.Min(total, Math.Max(clip, plate)) : total);
                    }
                }
                // The registration maps the stack's (x, y) to the sub's (x + u, y + v).
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        sum[c][(y * size) + x] += Image.Lanczos3Value(raw, size, size, (float)(x + u), (float)(y + v), Image.LanczosClampingThreshold);
                    }
                }
            }
        }
        var lum = new float[size * size];
        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += (float)(sum[c][i] / subs / channels);
            }
        }
        return lum;
    }

    private static float[] Mean(float[][] planes)
    {
        var mean = new float[planes[0].Length];
        foreach (var plane in planes)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += plane[i] / planes.Length;
            }
        }
        return mean;
    }

    // A region of unit-range plate planes with the given stars rendered as the Stars mode renders them, as luminance.
    private static float[] RenderLuminance(float[][] unitPlate, int width, BitMatrix? absent, int cx, int cy, int size, InjectedStar[] local, int seed)
    {
        var channels = unitPlate.Length;
        var basePlanes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            basePlanes[c] = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                unitPlate[c].AsSpan(((cy + y) * width) + cx, size).CopyTo(basePlanes[c].AsSpan(y * size, size));
            }
        }
        BitMatrix? regionAbsent = null;
        if (absent is { } frameAbsent)
        {
            var mask = new BitMatrix(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    mask[y, x] = frameAbsent[cy + y, cx + x];
                }
            }
            regionAbsent = mask;
        }
        var floors = Enumerable.Repeat(1e-7, channels).ToArray();
        var render = StarInjection.Render(basePlanes, size, size, regionAbsent, local, floors, new Random(seed ^ 0x6d2b79f5));
        return Mean(render.Planes);
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
