using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance retrofit's check on a real master with real models (#2534): a project whose image <c>enhanced</c> is the
/// one-step enhance of a 10P master and whose <c>chain</c> is that enhance laid out as its single steps
/// (<c>tianwen workbench chain</c>), both already run with RC-Astro, compared ring excluded. <c>EnhanceChainTests</c>
/// holds the two to each other pixel for pixel on stand-in enhancers; this reads them with the real ones.
/// </summary>
/// <remarks>
/// <para><b>Env-gated.</b> <c>TIANWEN_ENHANCE_10P_PROJECT</c> is the project's folder, run (<c>tianwen workbench run</c>):
/// each image's last edit names its stored result. <c>TIANWEN_ENHANCE_10P_REPEAT</c>, optional, is a second project
/// holding the same <c>enhanced</c> image, run on its own store, so the enhance ran twice: what RC-Astro's GPU path
/// leaves between two runs of one tool is then measured on this machine, and the chain is held to it. Without it the
/// probe checks its premises, prints the reading and reports itself skipped, so a run with no null to hold the chain to
/// never reads as a pass.</para>
/// <para><b>What is read</b> (the measure of 2026-11-24, in .NET): the sky level and the green sky sigma of each
/// result; the pixels whose value differs by more than <see cref="DifferBy"/> in any channel, and how many of them lie on
/// a star (the chain's own stars layer, its result less <c>chain-starless</c>, over five times the starless plate's green
/// sky sigma, grown by <see cref="StarGrowPx"/>); each channel's peak; the green share of the star light (each result less its own sky
/// level, summed over the star mask). The bars were set before any 10P result of the retrofit was read.</para>
/// </remarks>
public sealed class EnhanceChain10PProbe(ITestOutputHelper output)
{
    private const float DifferBy = 1e-4f;
    private const int StarGrowPx = 2;
    private const float StarSigmas = 5f;

    [Fact(Timeout = 600_000)]
    public async Task TheChainReadsAsTheEnhanceOnTenP()
    {
        var folder = Environment.GetEnvironmentVariable("TIANWEN_ENHANCE_10P_PROJECT") ?? "";
        Assert.SkipWhen(folder.Length == 0, "set TIANWEN_ENHANCE_10P_PROJECT to a run project with images enhanced and chain to run the 10P enhance probe");
        var repeatFolder = Environment.GetEnvironmentVariable("TIANWEN_ENHANCE_10P_REPEAT") ?? "";
        var ct = TestContext.Current.CancellationToken;

        var enhanced = await ResultAsync(folder, "enhanced", ct);
        var chain = await ResultAsync(folder, "chain", ct);
        var starless = await ResultAsync(folder, "chain-starless", ct);
        enhanced.Shape.ShouldBe(chain.Shape, "premise: the enhance and its chain give one geometry");
        starless.Shape.ShouldBe(chain.Shape, "premise: the chain's starless plate is on the chain's geometry");
        enhanced.ChannelCount.ShouldBe(3, "premise: 10P is a colour master");

        var ring = Union(enhanced.AbsentPixels(), chain.AbsentPixels(), enhanced.Width, enhanced.Height);
        var starMask = StarMask(chain, starless, ring);
        var a = Read(enhanced, ring, starMask);
        var b = Read(chain, ring, starMask);
        var diff = Differ(enhanced, chain, ring, starMask);
        output.WriteLine($"10P enhance against its chain: {enhanced.Width} x {enhanced.Height}, {ring.PopCount()} ring px left out, {starMask.PopCount()} px on stars");
        output.WriteLine($"  sky level (green, off stars): enhance {a.SkyLevel:G6}, chain {b.SkyLevel:G6}");
        output.WriteLine($"  green sky sigma: enhance {a.GreenSigma:G4}, chain {b.GreenSigma:G4}");
        output.WriteLine($"  peaks R/G/B: enhance {a.Peaks[0]:G5}/{a.Peaks[1]:G5}/{a.Peaks[2]:G5}, chain {b.Peaks[0]:G5}/{b.Peaks[1]:G5}/{b.Peaks[2]:G5}");
        output.WriteLine($"  green share of star light: enhance {a.GreenShare:F3}, chain {b.GreenShare:F3}");
        output.WriteLine($"  differing by over {DifferBy:G}: {diff.Count} px of {diff.Read} ({diff.OnStars} on stars), largest {diff.Largest:G4}");

        // The premises a comparison needs; the agreement itself is held below only against a measured null.
        diff.Read.ShouldBeGreaterThan(0, "premise: pixels outside the ring to compare");
        starMask.PopCount().ShouldBeGreaterThan(0, "premise: the stars layer found stars");

        // A run without the second enhance has no measured null to hold the chain to, so it prints the reading and is
        // reported as skipped, never as a pass that reads like agreement.
        Assert.SkipWhen(repeatFolder.Length == 0,
            "the reading is printed above; set TIANWEN_ENHANCE_10P_REPEAT to a project that ran the enhance again to hold the chain to RC-Astro's own run-to-run difference");

        var repeat = await ResultAsync(repeatFolder, "enhanced", ct);
        repeat.Shape.ShouldBe(enhanced.Shape, "premise: the second enhance is of the same master");
        var nul = Differ(enhanced, repeat, ring, starMask);
        output.WriteLine($"  the enhance against itself (two runs): {nul.Count} px over {DifferBy:G} ({nul.OnStars} on stars), largest {nul.Largest:G4}");

        // Bars set before the retrofit's 10P results were read (#2534): the chain differs from the enhance by no more
        // than twice what two runs of the enhance differ by, its sky by under half a percent, each peak by under one.
        diff.Count.ShouldBeLessThanOrEqualTo(Math.Max(2L * nul.Count, 100L),
            "the chain differs from the enhance by more than RC-Astro's own run-to-run difference: a step the chain does not run as the enhance does");
        Math.Abs(b.SkyLevel - a.SkyLevel).ShouldBeLessThanOrEqualTo(0.005 * Math.Abs(a.SkyLevel), "the chain's sky level is not the enhance's");
        for (var c = 0; c < 3; c++)
        {
            Math.Abs(b.Peaks[c] - a.Peaks[c]).ShouldBeLessThanOrEqualTo(0.01 * a.Peaks[c], $"channel {c}'s peak differs: a scale handed on differently");
        }
    }

    // The stored result of an image's last edit, read through its record in project.json.
    private static async Task<Image> ResultAsync(string folder, string name, System.Threading.CancellationToken cancellationToken)
    {
        var (project, refusal) = await WorkbenchProjectFile.ReadAsync(folder, cancellationToken);
        project.ShouldNotBeNull(refusal);
        var image = project.Images.FirstOrDefault(each => each.Name == name).ShouldNotBeNull($"{folder} has no image {name}");
        var last = image.Edits.LastOrDefault().ShouldNotBeNull($"{name} has no edits");
        var stored = last.Output.ShouldNotBeNull($"{name} has no stored result: run the project first");
        Image.TryReadFitsFile(Path.Combine(folder, stored), out var read).ShouldBeTrue($"{name}'s result {stored} reads as FITS");
        return read ?? throw new InvalidOperationException($"{name}'s result {stored} read as nothing");
    }

    private static BitMatrix Union(BitMatrix? first, BitMatrix? second, int width, int height)
    {
        var union = new BitMatrix(height, width);
        foreach (var each in new[] { first, second })
        {
            if (each is { } ring)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = ring.NextSetBit(y, 0); x >= 0; x = ring.NextSetBit(y, x + 1))
                    {
                        union[y, x] = true;
                    }
                }
            }
        }

        return union;
    }

    // The stars: the chain's stars layer (its result less its starless plate, what add-stars put back), its mean over the
    // channels above StarSigmas of the starless plate's green sky sigma, grown by StarGrowPx.
    private static BitMatrix StarMask(Image chain, Image starless, BitMatrix ring)
    {
        var (_, width, height) = chain.Shape;
        starless.TryEstimateSkyNoise(1, ring, out var sigma).ShouldBeTrue("premise: the starless plate has sky to read its noise on");
        ReadOnlySpan<float> r = chain.GetChannelSpan(0), g = chain.GetChannelSpan(1), b = chain.GetChannelSpan(2);
        ReadOnlySpan<float> rs = starless.GetChannelSpan(0), gs = starless.GetChannelSpan(1), bs = starless.GetChannelSpan(2);
        var core = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (!ring[y, x] && (r[i] - rs[i] + g[i] - gs[i] + b[i] - bs[i]) / 3f > StarSigmas * sigma)
                {
                    core[y, x] = true;
                }
            }
        }

        var grown = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = core.NextSetBit(y, 0); x >= 0; x = core.NextSetBit(y, x + 1))
            {
                for (var dy = -StarGrowPx; dy <= StarGrowPx; dy++)
                {
                    for (var dx = -StarGrowPx; dx <= StarGrowPx; dx++)
                    {
                        var yy = y + dy;
                        var xx = x + dx;
                        if (yy >= 0 && yy < height && xx >= 0 && xx < width && !ring[yy, xx])
                        {
                            grown[yy, xx] = true;
                        }
                    }
                }
            }
        }

        return grown;
    }

    private sealed record Reading(double SkyLevel, double GreenSigma, double[] Peaks, double GreenShare);

    private static Reading Read(Image image, BitMatrix ring, BitMatrix stars)
    {
        var (_, width, height) = image.Shape;
        var levels = new double[3];
        var peaks = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var plane = image.GetChannelSpan(c);
            var sky = new float[width * height];
            var n = 0;
            var peak = double.NegativeInfinity;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (ring[y, x])
                    {
                        continue;
                    }

                    var v = plane[y * width + x];
                    // A non-finite value is kept out of the peak as of the sky: Math.Max(x, NaN) is NaN, and a NaN peak
                    // would pass the peak bar whatever the real peaks are.
                    if (!float.IsFinite(v))
                    {
                        continue;
                    }

                    peak = Math.Max(peak, v);
                    if (!stars[y, x])
                    {
                        sky[n++] = v;
                    }
                }
            }

            double.IsFinite(peak).ShouldBeTrue($"premise: channel {c} has a finite value outside the ring to take a peak of");

            Array.Sort(sky, 0, n);
            levels[c] = n == 0 ? double.NaN : sky[n / 2];
            peaks[c] = peak;
        }

        var light = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = stars.NextSetBit(y, 0); x >= 0; x = stars.NextSetBit(y, x + 1))
                {
                    light[c] += plane[y * width + x] - levels[c];
                }
            }
        }

        image.TryEstimateSkyNoise(1, ring, out var greenSigma).ShouldBeTrue("premise: the result has sky to read its green noise on");
        return new Reading(levels[1], greenSigma, peaks, light[1] / (light[0] + light[1] + light[2]));
    }

    private sealed record Difference(long Count, long OnStars, long Read, double Largest);

    private static Difference Differ(Image first, Image second, BitMatrix ring, BitMatrix stars)
    {
        var (_, width, height) = first.Shape;
        ReadOnlySpan<float> r1 = first.GetChannelSpan(0), g1 = first.GetChannelSpan(1), b1 = first.GetChannelSpan(2);
        ReadOnlySpan<float> r2 = second.GetChannelSpan(0), g2 = second.GetChannelSpan(1), b2 = second.GetChannelSpan(2);
        long count = 0, onStars = 0, read = 0;
        var largest = 0.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (ring[y, x])
                {
                    continue;
                }

                read++;
                var i = y * width + x;
                // A value finite in one result and not in the other differs, whatever its size; it is counted, and kept
                // out of the largest so a NaN does not hide the largest real difference.
                var oneSided = float.IsFinite(r1[i]) != float.IsFinite(r2[i]) || float.IsFinite(g1[i]) != float.IsFinite(g2[i])
                    || float.IsFinite(b1[i]) != float.IsFinite(b2[i]);
                var worst = Math.Max(Gap(r1[i], r2[i]), Math.Max(Gap(g1[i], g2[i]), Gap(b1[i], b2[i])));
                largest = Math.Max(largest, worst);
                if (oneSided || worst > DifferBy)
                {
                    count++;
                    if (stars[y, x])
                    {
                        onStars++;
                    }
                }
            }
        }

        return new Difference(count, onStars, read, largest);
    }

    // The gap between two values where both are finite; 0 where either is not (one-sided values are counted apart).
    private static double Gap(float first, float second)
        => float.IsFinite(first) && float.IsFinite(second) ? Math.Abs(first - second) : 0.0;
}
