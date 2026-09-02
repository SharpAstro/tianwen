using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the in-house Noise2Noise denoiser. The model ships IN the repo at
/// <c>src/TianWen.AI.Imaging/models/</c> (copied beside every consumer's binaries as
/// <c>models/</c> by that project's Content item), so the resolver probes that copy first and
/// falls back to the per-user cache dirs. At 3.1 MiB it is committed as a plain git blob rather
/// than an LFS object -- <c>.gitattributes</c> exempts that directory from the repo-wide
/// <c>*.onnx</c> LFS rule, to keep infrequent clones off the LFS bandwidth budget -- so a checkout
/// has the real weights whether or not git-lfs is installed. <see cref="ModelResolver"/> still
/// refuses a pointer stub, which is what keeps the model-gated tests skipping rather than failing
/// if that ever changes back.
/// <para>
/// <b>The parity test is the point of this file.</b> The exported graph is already pinned against
/// torch by <c>n2n-smoke/ship/n2n_export.py</c> (max |diff| 1.49e-7). What no Python check can
/// cover is the C# deployment path around it: the whole-frame MTF into the training domain and its
/// inverse, NCHW packing, the median-fill border, the 256 px chunking, the replicate-pad of an edge
/// chunk, the per-channel level restore, the stitch that drops a 16 px rim, and the blend. Every one
/// of those fails silently -- a transposed tensor or a mis-scaled input still produces a
/// plausible-looking picture, and a frame fed 100x below the training band did for two weeks.
/// </para>
/// </summary>
[Collection("Imaging")]
public class N2nDenoiserTests(ITestOutputHelper output)
{
    private const string FixtureResource = "TianWen.Lib.Tests.Data.n2n-parity-fixture.json";

    /// <summary>
    /// Deliberately the DEFAULT resolver, not a hand-built search list. Its first entry is now the
    /// app-local <c>models/</c> -- the checkout's own LFS copy, landed beside the binaries by
    /// TianWen.AI.Imaging -- then the per-user caches, so CI (which narrow-pulls <c>*.onnx</c> and
    /// has no cache) still runs the parity test against exactly the weights being shipped.
    ///
    /// <para>This used to prepend that directory itself, which is what let the apps' inability to
    /// find the same file go unnoticed for as long as it did: the suite was green against a search
    /// path no shipped binary used. Sharing the default is the regression guard.</para>
    /// </summary>
    private static ModelResolver CreateResolver() => new ModelResolver();

    private static bool HasModel(out string skipMessage)
    {
        if (CreateResolver().TryResolve(N2nDenoiser.ModelFileName, out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = $"{N2nDenoiser.ModelFileName} not found (or is an unmaterialized LFS pointer); run 'git lfs pull' or tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    private static JsonElement LoadFixture()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(FixtureResource).ShouldNotBeNull();
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The plate the Python fixture generator builds, restated. Both sides run the LCG explicitly
    /// (<c>s = s * 1664525 + 1013904223 mod 2^32</c>) because no two runtimes' built-in RNGs agree
    /// -- <see cref="Random"/> and <c>numpy.random</c> least of all -- and shipping the plate as a
    /// fixture would mean 300 KiB of incompressible noise that still has to be read identically at
    /// both ends to be worth anything.
    /// </summary>
    private static Image BuildPlate(int size, int channels, float background, float noiseAmplitude, int starCount, uint seed)
    {
        var state = seed;
        float NextUnit()
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return (float)(state / 4294967296.0);
        }

        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++) plane[y, x] = background;
            }
            planes[c] = plane;
        }

        for (var s = 0; s < starCount; s++)
        {
            var cx = NextUnit() * size;
            var cy = NextUnit() * size;
            var amp = 0.05f + 0.60f * NextUnit();
            var sigma = 1.2f + 1.8f * NextUnit();
            var x0 = Math.Max(0, (int)(cx - 8));
            var x1 = Math.Min(size, (int)(cx + 9));
            var y0 = Math.Max(0, (int)(cy - 8));
            var y1 = Math.Min(size, (int)(cy + 9));
            for (var c = 0; c < channels; c++)
            {
                var weight = 0.7 + 0.3 * ((c + 1.0) / channels);
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                        planes[c][y, x] += (float)(amp * Math.Exp(-d2 / (2.0 * sigma * sigma)) * weight);
                    }
                }
            }
        }

        // Grain last, and in channel-then-row-then-column order: the LCG is a single stream, so
        // the traversal order is part of the plate's definition.
        for (var c = 0; c < channels; c++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][y, x] = Math.Clamp(planes[c][y, x] + noiseAmplitude * (NextUnit() - 0.5f), 0f, 1f);
                }
            }
        }

        // One dead pixel per channel, after the grain so the LCG stream is untouched. It is the one
        // thing a real frame has that a flat synthetic sky does not: a minimum far below the level.
        // The runner's auto-detect (the exporter's NeedsStretch) keys on median MINUS min, so without
        // it a plate at 0.26 with +-0.02 grain reads as linear and is stretched, and the parity
        // fixture, which torch computed on the raw plate, stops applying. With it the parity plate is
        // in band the way every training tile was (min 0, median near 0.25) and is fed as it is.
        for (var c = 0; c < channels; c++) planes[c][0, 0] = 0f;

        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    private static Image BuildPlateFrom(JsonElement fixture) => BuildPlate(
        fixture.GetProperty("size").GetInt32(),
        fixture.GetProperty("channels").GetInt32(),
        fixture.GetProperty("background").GetSingle(),
        fixture.GetProperty("noise_amplitude").GetSingle(),
        fixture.GetProperty("star_count").GetInt32(),
        (uint)fixture.GetProperty("seed").GetInt64());

    /// <summary>
    /// The plate itself must match first. If this fails, nothing downstream means anything -- and
    /// the failure is in the generator restatement, not in the denoiser.
    /// </summary>
    [Fact]
    public void ThePlateGeneratorAgreesWithPython()
    {
        var fixture = LoadFixture();
        var plate = BuildPlateFrom(fixture);
        var expectedMean = fixture.GetProperty("input").GetProperty("mean").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var expectedStd = fixture.GetProperty("input").GetProperty("std").EnumerateArray().Select(e => e.GetSingle()).ToArray();

        for (var c = 0; c < plate.ChannelCount; c++)
        {
            var span = plate.GetChannelSpan(c);
            double sum = 0;
            for (var i = 0; i < span.Length; i++) sum += span[i];
            var mean = sum / span.Length;
            double sq = 0;
            for (var i = 0; i < span.Length; i++) sq += (span[i] - mean) * (span[i] - mean);
            var std = Math.Sqrt(sq / span.Length);

            output.WriteLine($"ch{c}: mean {mean:F6} (want {expectedMean[c]:F6}), std {std:F6} (want {expectedStd[c]:F6})");
            mean.ShouldBe(expectedMean[c], 1e-5);
            std.ShouldBe(expectedStd[c], 1e-5);
        }

        // Every sampled input pixel too: a mean and a std would survive a transposed plate.
        foreach (var sample in fixture.GetProperty("samples").EnumerateArray())
        {
            var c = sample.GetProperty("c").GetInt32();
            var x = sample.GetProperty("x").GetInt32();
            var y = sample.GetProperty("y").GetInt32();
            var expected = sample.GetProperty("in").GetSingle();
            plate.GetChannelSpan(c)[y * fixture.GetProperty("size").GetInt32() + x].ShouldBe(expected, 1e-5f);
        }
    }

    /// <summary>
    /// The whole C# path against torch's answer for the same plate. The fixture's 160 px size is
    /// load-bearing: it borders to 192, which <c>ChunkedInference.Split</c> yields as exactly ONE
    /// chunk (its step is 192 and its loop runs while <c>i &lt; height</c>), so a discrepancy is
    /// attributable rather than averaged away across overlapping tiles. That chunk is still
    /// replicate-padded 192 -> 256 to meet the model's declared tile, so the edge-chunk path every
    /// real image takes at its right and bottom margins is covered here too.
    /// <para>The plate's background of 0.26 is above the 0.125 auto-detect, so the runner feeds it
    /// as it is and torch saw the same bytes: this pins the graph and the tiling, and by construction
    /// it cannot see whether a LINEAR frame is stretched first, which is what
    /// <see cref="ALinearInputTakesTheExportersStretchAndComesBackInItsOwnUnits"/> is for. A plate
    /// at 0.26 is also, not by accident, where every training tile sat.</para>
    /// </summary>
    [Fact]
    public async Task TheWholePipelineReproducesTorch()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var fixture = LoadFixture();
        var size = fixture.GetProperty("size").GetInt32();
        var plate = BuildPlateFrom(fixture);

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new N2nDenoiser(CreateResolver(), factory.CreateLogger<N2nDenoiser>());
        var result = await enhancer.EnhanceAsync(plate, 1.0f, TestContext.Current.CancellationToken);

        var (channels, w, h) = result.Shape;
        channels.ShouldBe(fixture.GetProperty("channels").GetInt32());
        w.ShouldBe(size);
        h.ShouldBe(size);

        var expectedMean = fixture.GetProperty("output").GetProperty("mean").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var expectedStd = fixture.GetProperty("output").GetProperty("std").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        for (var c = 0; c < channels; c++)
        {
            var span = result.GetChannelSpan(c);
            double sum = 0;
            for (var i = 0; i < span.Length; i++) sum += span[i];
            var mean = sum / span.Length;
            double sq = 0;
            for (var i = 0; i < span.Length; i++) sq += (span[i] - mean) * (span[i] - mean);
            var std = Math.Sqrt(sq / span.Length);
            output.WriteLine($"ch{c}: mean {mean:F6} (torch {expectedMean[c]:F6}), std {std:F6} (torch {expectedStd[c]:F6})");
            // 1e-4 rather than the graph's own 1e-7: this compares ORT against torch across two
            // runtimes and two execution providers, so it is a correctness bound, not a
            // bit-exactness one. A packing, border, level or blend defect misses by far more.
            mean.ShouldBe(expectedMean[c], 1e-4);
            std.ShouldBe(expectedStd[c], 1e-4);
        }

        var worst = 0.0f;
        foreach (var sample in fixture.GetProperty("samples").EnumerateArray())
        {
            var c = sample.GetProperty("c").GetInt32();
            var x = sample.GetProperty("x").GetInt32();
            var y = sample.GetProperty("y").GetInt32();
            var got = result.GetChannelSpan(c)[y * size + x];
            worst = Math.Max(worst, Math.Abs(got - sample.GetProperty("out").GetSingle()));
        }
        output.WriteLine($"worst sampled-pixel difference vs torch: {worst:E3}");
        worst.ShouldBeLessThan(1e-3f);

        result.Release();
    }

    /// <summary>
    /// A linear frame is stretched into the training domain by the runner itself and inverted with
    /// the same parameters, so doing those two steps by hand around the denoiser gives the identical
    /// answer: the runner's auto-detect finds the hand-stretched plate already in band and feeds it
    /// as it is. That equality is the pin. Until 2026-09-02 the runner fed a linear plate verbatim
    /// and the two routes disagreed by 0.89 on the Bubble master; the parity plate cannot see that,
    /// because at a background of 0.26 it is already where the training tiles were.
    /// </summary>
    [Fact]
    public async Task ALinearInputTakesTheExportersStretchAndComesBackInItsOwnUnits()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        // The seam master's level and noise (median 0.0019, MAD near 1e-4), in one 160 px chunk.
        var plate = BuildPlate(160, channels: 3, background: 0.002f, noiseAmplitude: 0.0003f, starCount: 25, seed: 20260902u);
        using var enhancer = new N2nDenoiser(CreateResolver());
        var ct = TestContext.Current.CancellationToken;

        var direct = await enhancer.EnhanceAsync(plate, 1.0f, ct);

        var (stretched, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(plate);
        applied.ShouldBeTrue("a plate at 0.002 is linear by the exporter's own test");
        var sorted = stretched.GetChannelSpan(0).ToArray();
        Array.Sort(sorted);
        sorted[sorted.Length / 2].ShouldBe((float)AiNafnetInputs.TargetMedian, 0.01f);
        var inBand = await enhancer.EnhanceAsync(stretched, 1.0f, ct);
        var byHand = inBand.MtfUnstretch(origMin!, balances!);

        var worst = 0f;
        var moved = 0f;
        for (var c = 0; c < plate.ChannelCount; c++)
        {
            var a = direct.GetChannelSpan(c);
            var b = byHand.GetChannelSpan(c);
            var src = plate.GetChannelSpan(c);
            for (var i = 0; i < a.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
                moved = Math.Max(moved, Math.Abs(a[i] - src[i]));
            }
        }
        output.WriteLine($"runner vs by-hand stretch: max |diff| {worst:E3}; runner vs input: max |diff| {moved:E3}");
        worst.ShouldBeLessThan(1e-6f);
        // It denoised: an identity runner would pass the line above too.
        moved.ShouldBeGreaterThan(1e-4f);

        direct.Release();
        inBand.Release();
    }

    /// <summary>
    /// The user-facing dial is a blend, so it must be exactly linear in the model's output and
    /// must reach the untouched input as it goes to zero. Both are checked against the model's own
    /// full-strength answer rather than against a fixture, so this holds for any future checkpoint.
    /// </summary>
    [Fact]
    public async Task TheStrengthDialIsALinearBlendTowardTheInput()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var fixture = LoadFixture();
        var size = fixture.GetProperty("size").GetInt32();
        var plate = BuildPlateFrom(fixture);
        using var enhancer = new N2nDenoiser(CreateResolver());

        var full = await enhancer.EnhanceAsync(plate, 1.0f, TestContext.Current.CancellationToken);
        var half = await enhancer.EnhanceAsync(plate, 0.5f, TestContext.Current.CancellationToken);
        var faint = await enhancer.EnhanceAsync(plate, 0.01f, TestContext.Current.CancellationToken);

        var worstHalf = 0.0f;
        var worstFaint = 0.0f;
        for (var c = 0; c < plate.ChannelCount; c++)
        {
            var src = plate.GetChannelSpan(c);
            var f = full.GetChannelSpan(c);
            var hh = half.GetChannelSpan(c);
            var ff = faint.GetChannelSpan(c);
            for (var i = 0; i < src.Length; i++)
            {
                worstHalf = Math.Max(worstHalf, Math.Abs(hh[i] - (src[i] + 0.5f * (f[i] - src[i]))));
                worstFaint = Math.Max(worstFaint, Math.Abs(ff[i] - src[i]));
            }
        }
        output.WriteLine($"worst |blend(0.5) - midpoint| {worstHalf:E3}, worst |blend(0.01) - input| {worstFaint:E3}");
        worstHalf.ShouldBeLessThan(1e-5f);
        // At 1% the output must be within 1% of the model's excursion from the input, which for
        // this plate is a small number -- the assertion is that the dial genuinely approaches the
        // untouched input rather than bottoming out, the failure mode the conditioning dial has.
        worstFaint.ShouldBeLessThan(0.01f);

        full.Release();
        half.Release();
        faint.Release();
        _ = size;
    }

    [Fact]
    public async Task MonoIsRejectedRatherThanTiledAcrossTheColourSlots()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var mono = new Image([new float[64, 64]], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Monochrome });
        using var enhancer = new N2nDenoiser(CreateResolver());

        var ex = await Should.ThrowAsync<NotSupportedException>(
            async () => await enhancer.EnhanceAsync(mono, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("one-shot-colour");
    }

    [Fact]
    public async Task OutOfRangeInputIsRejectedWithThePointerToNormalisation()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var src = new Image([new float[16, 16], new float[16, 16], new float[16, 16]],
            BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });
        using var enhancer = new N2nDenoiser(CreateResolver());

        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("AdoptImageAsync");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public async Task AStrengthOutsideTheUnitIntervalIsRejected(float strength)
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var src = new Image([new float[16, 16], new float[16, 16], new float[16, 16]],
            BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        using var enhancer = new N2nDenoiser(CreateResolver());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await enhancer.EnhanceAsync(src, strength, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The N2N model is opt-in and must not displace <see cref="OnnxDenoiser"/> by merely being
    /// registered: it is OSC-only, and it has never been compared against the AI4 denoiser on the
    /// enhance pipeline's own job. Both halves of that are asserted here so a future
    /// <c>TryAddSingleton</c> slip in <c>AddTianWenAi</c> shows up as a red test.
    /// </summary>
    [Fact]
    public void TheDefaultRegistrationKeepsTheAi4DenoiserAndTheOptInReplacesIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTianWenAi();
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<IDenoiseEnhancer>().ShouldBeOfType<OnnxDenoiser>();
        }

        var optIn = new ServiceCollection();
        optIn.AddSingleton(NullLoggerFactory.Instance);
        optIn.AddLogging();
        optIn.AddTianWenN2nDenoiser();
        using (var provider = optIn.BuildServiceProvider())
        {
            var denoiser = provider.GetRequiredService<IDenoiseEnhancer>();
            denoiser.ShouldBeOfType<N2nDenoiser>();
            denoiser.Name.ShouldContain("N2N");
        }
    }

    /// <summary>
    /// One weight bundle exists, so a caller asking for Lite or Walking is told rather than
    /// silently handed Default -- the variant axis belongs to the AI4 family, not to this model.
    /// Both routes in: the variant overload and the pipeline's variant+options overload.
    /// </summary>
    [Fact]
    public async Task ANonDefaultVariantIsRefusedRatherThanIgnored()
    {
        var src = new Image([new float[16, 16], new float[16, 16], new float[16, 16]],
            BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        using var enhancer = new N2nDenoiser(CreateResolver());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await enhancer.EnhanceAsync(src, DenoiseVariant.Lite, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await enhancer.EnhanceAsync(src, DenoiseVariant.Walking, EnhanceOptions.Default,
                progress: null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <c>--denoise-strength</c> reaches this model: the pipeline calls the variant+options overload,
    /// and <see cref="EnhanceTuning.DenoiseStrength"/> must land on the blend dial -- the same
    /// deterministic path as the direct strength overload, so the two answers are identical.
    /// </summary>
    [Fact]
    public async Task TheTuningDenoiseStrengthDrivesTheBlend()
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        var fixture = LoadFixture();
        var plate = BuildPlateFrom(fixture);
        using var enhancer = new N2nDenoiser(CreateResolver());

        var viaOptions = await enhancer.EnhanceAsync(plate, DenoiseVariant.Default,
            new EnhanceOptions(EnhanceBackend.N2n, new EnhanceTuning(DenoiseStrength: 0.5f)),
            progress: null, TestContext.Current.CancellationToken);
        var direct = await enhancer.EnhanceAsync(plate, 0.5f, TestContext.Current.CancellationToken);

        for (var c = 0; c < plate.ChannelCount; c++)
        {
            viaOptions.GetChannelSpan(c).SequenceEqual(direct.GetChannelSpan(c)).ShouldBeTrue(
                $"channel {c}: the options path must be the same computation as the direct strength path");
        }

        viaOptions.Release();
        direct.Release();
    }

    /// <summary>
    /// The end-to-end guard for the tile seams, and the one test here that stitches at all: the
    /// parity plate is 160 px against a 256 px tile, so it is a SINGLE chunk and every defect in
    /// the join is invisible to it. This plate is 512 px, which is 9 chunks.
    /// </summary>
    /// <remarks>
    /// <para><b>The background is the level of the real master this was reported on (0.002), so the
    /// plate takes the path a master takes end to end:</b> the runner stretches it to the training
    /// median, denoises, and inverts. The net carries a learned sky-level prior and <c>RestoreLevel</c>
    /// corrects the resulting drag per chunk, so the SIZE of the per-chunk disagreement grows as the
    /// input level departs from the training band; before the runner stretched for itself, a linear
    /// 0.002 sat 100x below that band, the drag was 39 times the sky level, and an unweighted stitch
    /// drew a grid that the feather now has to hide. At an in-distribution 0.26 the disagreement is
    /// small enough that this test would pass while asserting nothing, which is why the plate stays
    /// linear rather than being handed to the net pre-stretched.</para>
    ///
    /// <para>The seam positions are derived, not guessed: chunks step <c>tile - overlap</c> and keep
    /// <c>tile - 2 * border</c>, so consecutive retained regions share <c>overlap - 2 * border</c>
    /// px starting at each multiple of the stride.</para>
    /// </remarks>
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public async Task TheChunkSeamsDoNotShowInTheStitchedOutput(int overlap)
    {
        if (!HasModel(out var skip)) { Assert.Skip(skip); return; }

        // Big enough that INTERIOR chunks dominate at every geometry under test. At 512 with a
        // 128 stride only four chunks fit and two of them are outer ones, so the documented
        // outer-chunk residual below would set the median all by itself.
        const int size = 1024;
        const int tile = 256;
        const float background = 0.002f;
        var border = AiNafnetInputs.StitchBorderPx;
        var stride = tile - overlap;
        var band = overlap - 2 * border;

        var plate = BuildPlate(size, channels: 3, background: background,
            noiseAmplitude: 0.0006f, starCount: 25, seed: 20260820u);
        using var enhancer = new N2nDenoiser(CreateResolver(), overlap: overlap);
        var denoised = await enhancer.EnhanceAsync(plate, 1.0f, TestContext.Current.CancellationToken);

        var worst = 0.0;
        var allRatios = new List<double>();
        for (var c = 0; c < plate.ChannelCount; c++)
        {
            var src = plate.GetChannelSpan(c);
            var dst = denoised.GetChannelSpan(c);

            // Column means of the CORRECTION, which removes the plate and leaves the per-chunk DC.
            var profile = new double[size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++) profile[x] += dst[y * size + x] - src[y * size + x];
            }
            for (var x = 0; x < size; x++) profile[x] /= size;

            // Structure yardstick: the typical column-to-column change away from any seam edge.
            var offSeam = new List<double>();
            for (var x = 1; x < size; x++)
            {
                var phase = x % stride;
                if (phase <= band + 2 || phase >= stride - 2) continue;
                offSeam.Add(Math.Abs(profile[x] - profile[x - 1]));
            }
            offSeam.Sort();
            var baseline = offSeam[offSeam.Count / 2];

            for (var seam = stride; seam < size - 1; seam += stride)
            {
                foreach (var edge in new[] { seam, seam + band })
                {
                    if (edge < 1 || edge >= size) continue;
                    var ratio = Math.Abs(profile[edge] - profile[edge - 1]) / Math.Max(baseline, 1e-12);
                    output.WriteLine($"ch{c} x={edge}: step={Math.Abs(profile[edge] - profile[edge - 1]):E2} = {ratio:F1}x local");
                    worst = Math.Max(worst, ratio);
                    allRatios.Add(ratio);
                }
            }
        }

        allRatios.Sort();
        var medianRatio = allRatios[allRatios.Count / 2];
        output.WriteLine($"median seam-edge step {medianRatio:F1}x local structure, worst {worst:F1}x");

        // The MEDIAN, deliberately, and not the worst. Unweighted stitching makes EVERY seam edge
        // detectable -- measured on a real master, 38 of 38 edges sat at >=3x and the median was
        // 52x -- so a median near 1x is exactly the property the blend establishes, and it collapses
        // to the tens the moment the feather is removed.
        //
        // The worst is deliberately NOT asserted tightly, because a known residual survives at the
        // bands touching the OUTERMOST chunks and it is not the blend's to fix: AddBorder fills the
        // outer margin with the plane median and a clipped edge chunk is replicate-padded, so an
        // outer tile's input carries synthetic, noiseless pixels. This net measures its sigma
        // conditioning over the WHOLE tile (see N2nLinearRunner), so an outer chunk is conditioned
        // on a diluted sigma and denoises with different STRENGTH than its neighbour -- a
        // disagreement in character, which no reweighting of a convex combination can remove. It
        // moves with the stride (so it is chunk-related, not structure) and stays local to the first
        // and last bands.
        medianRatio.ShouldBeLessThan(5.0,
            "the stride positions must be statistically indistinguishable from anywhere else");

        denoised.Release();
        plate.Release();
    }
}
