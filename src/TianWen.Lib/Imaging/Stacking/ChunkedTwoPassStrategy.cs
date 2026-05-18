using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Streaming two-pass strategy with cross-N rejection. Pass A accumulates
/// per-pixel sum + sumSq + count over all N frames; pass B sigma-clips each
/// frame's pixel against the global mean ± kappa·sd and combines survivors.
/// Fidelity ~0.95 -- close to <see cref="InRamAllFramesStrategy"/>'s 1.0,
/// noticeably above the per-chunk rejection that this strategy historically
/// did (the original "ChunkedTwoPass" had per-chunk thresholds at fidelity
/// 0.80 because a 5σ outlier vs the full distribution might only be 3σ in
/// its 80-frame chunk).
/// </summary>
/// <remarks>
/// <para>The strategy iterates <see cref="IntegrationJob.WarpedFrames"/>
/// twice. On the test orchestrator's producer (Phase 8.5) the calibrated-
/// frame cache absorbs the second iteration's Load + Calibrate cost almost
/// entirely; only Debayer + Warp run twice per frame, plus the per-pixel
/// accumulation work. On a 244-frame SoL run that's roughly 2× the wall
/// clock of the old per-chunk rejection variant -- a small price for
/// ~16 percentage points of fidelity.</para>
///
/// <para><b>Memory profile</b> -- 3008² × 3 channels:
/// <list type="bullet">
///   <item>Pass A: 3 × (sum + sumSq) channels at float32 + 1 count channel at
///         uint32. ~252 MB total.</item>
///   <item>After pass A: derive mean + sd (replace sumSq with sd in-place to
///         reuse the array). Drop sum (the sums are subsumed by sd via the
///         identity sd² = (sumSq − sum²/n)/n). Keep mean + sd + count. ~252 MB.</item>
///   <item>Pass B: add weightedSum (3 ch) + keptCount (1). ~144 MB.</item>
///   <item>Peak: ~600 MB. Well under tight-host budgets and an order of
///         magnitude below the historical chunk-buffer approach's
///         12.5 GB at the same workload.</item>
/// </list></para>
///
/// <para>The "Chunked" half of the strategy name is historical -- there are
/// no chunks now. We kept the kind so existing selector tests + log lines
/// don't churn; the next selector revision can rename without a behaviour
/// change.</para>
/// </remarks>
public sealed class ChunkedTwoPassStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;

    public ChunkedTwoPassStrategy(IntegrationCostModel? costs = null)
    {
        _costs = costs ?? new IntegrationCostModel();
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.ChunkedTwoPass;

    /// <summary>Cross-N sigma-clip recovers most of the rejection fidelity
    /// the old per-chunk variant lost. Sits just under
    /// <see cref="InRamAllFramesStrategy"/>'s 1.0 because the producer
    /// re-iteration in pass B can pick up subtle drift from non-deterministic
    /// floating-point warp paths (Parallel.For ordering).</summary>
    public double FidelityScore => 0.95;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        var ramCap = budget.AllowedRam(probe);
        // Pass A: sum + sumSq per channel + count = 3 × canvasBytes (float)
        //   + 1 × (canvasBytes / 4 × 4) for count uint32. Round up to 4×.
        // Pass B: mean + sd + count carried over, plus weightedSum (channelCount × float)
        //   + keptCount (uint32). About another 2× canvasBytes.
        // One in-flight warped frame at canvasBytes. Output master + reject map.
        // 7× canvasBytes upper bound (loose; real peak ~6×).
        var ram = probe.CanvasBytes * 7 + probe.OutputRamBytes;
        var eta = _costs.LoadAndCalibrateAllFrames(probe)
            + _costs.DebayerAllFrames(probe) * 2
            + _costs.WarpAllFrames(probe) * 2
            + _costs.StackAllFrames(probe) * 2;

        if (ram > ramCap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: eta,
                Rationale: $"needs {Format.GB(ram)} RAM for global stats + accumulators, cap {Format.GB(ramCap)}");
        }

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"two-pass streaming reject+combine, {Format.GB(ram)} / {Format.GB(ramCap)}, cross-N sigma-clip")
        {
            FloorRamBytes = ram,
        };
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        var combiner = job.Options.Combiner ?? new MeanCombiner();
        var rejector = job.Options.Rejector;
        var n = job.ExpectedFrameCount;
        var swStrat = System.Diagnostics.Stopwatch.StartNew();

        // ----- Pass A: stream + accumulate per-pixel sum / sumSq / count -----
        // Allocated lazily off the first frame so we don't have to assume
        // canvas shape ahead of time.
        float[][,]? sumArr = null;
        float[][,]? sumSqArr = null;
        uint[,]? countArr = null;
        int channels = 0, width = 0, height = 0;
        Image? firstFrame = null;
        var framesSeen = 0;
        long totalRejections = 0;

        await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
        {
            firstFrame ??= warped;
            if (sumArr is null)
            {
                (channels, width, height) = warped.Shape;
                sumArr = new float[channels][,];
                sumSqArr = new float[channels][,];
                for (var c = 0; c < channels; c++)
                {
                    sumArr[c] = new float[height, width];
                    sumSqArr[c] = new float[height, width];
                }
                countArr = new uint[height, width];
            }

            var normalised = NormaliseIfRequested(warped, job);
            AccumulateStats(normalised, sumArr, sumSqArr!, countArr!);
            framesSeen++;
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.LoadingFrames, framesSeen, n, swStrat.Elapsed));
        }

        if (sumArr is null || sumSqArr is null || countArr is null || firstFrame is null || framesSeen == 0)
        {
            throw new InvalidOperationException("ChunkedTwoPass: producer yielded no frames");
        }

        // Derive per-pixel mean + sd in place. sumSqArr becomes sdArr.
        // After this loop sumArr holds the per-pixel mean and sumSqArr holds
        // the per-pixel standard deviation; both have NaN where count==0
        // (pixels that no frame covered, typically union-BB edges).
        var meanArr = sumArr;
        var sdArr = sumSqArr;
        FinaliseMeanAndSd(meanArr, sdArr, countArr);

        // ----- Pass B: stream again, sigma-clip + combine -----
        // Re-iterate the producer. The Phase 8.5 calibrated-frame cache on
        // the test orchestrator makes this near-free for Load + Calibrate;
        // only Debayer + Warp run twice per frame.
        var (kappaLow, kappaHigh) = ExtractKappa(rejector);
        var combineSum = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            combineSum[c] = new float[height, width];
        }
        var keptCount = new uint[height, width];
        var passBFrames = 0;

        await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            var normalised = NormaliseIfRequested(warped, job);
            var localRejected = AccumulateClippedCombine(normalised, meanArr, sdArr, kappaLow, kappaHigh, combineSum, keptCount);
            totalRejections += localRejected;
            passBFrames++;
            job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Integrating, passBFrames, framesSeen, swStrat.Elapsed));
        }

        if (passBFrames != framesSeen)
        {
            throw new InvalidOperationException(
                $"ChunkedTwoPass: producer yielded {passBFrames} frames in pass B vs {framesSeen} in pass A. " +
                "WarpedFrames must be repeatable for two-pass strategies.");
        }

        // ----- Finalise master + rejection map -----
        job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Finalizing, 0, 1, swStrat.Elapsed));
        var masterData = Image.CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var masterCh = masterData[c];
            var sumCh = combineSum[c];
            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var k = keptCount[y, x];
                    masterCh[y, x] = k > 0 ? sumCh[y, x] / k : float.NaN;
                }
            });
        }

        var rejectMapData = Image.CreateChannelData(1, height, width);
        var rejectMap = rejectMapData[0];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                rejectMap[y, x] = framesSeen > 0
                    ? Math.Max(0f, 1f - (float)keptCount[y, x] / framesSeen)
                    : 0f;
            }
        });

        var meanRate = framesSeen > 0
            ? (double)totalRejections / ((double)framesSeen * width * height * channels)
            : 0.0;

        var masterImage = new Image(
            data: masterData, bitDepth: BitDepth.Float32,
            maxValue: firstFrame.MaxValue, minValue: 0f,
            pedestal: firstFrame.Pedestal, imageMeta: firstFrame.ImageMeta);
        var rejectMapImage = new Image(
            data: rejectMapData, bitDepth: BitDepth.Float32,
            maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: firstFrame.ImageMeta);

        job.Progress?.Report(new IntegrationProgress(IntegrationPhase.Finalizing, 1, 1, swStrat.Elapsed));
        return new IntegrationResult(masterImage, rejectMapImage, framesSeen, totalRejections, meanRate);
    }

    /// <summary>Normalise the warped frame using stats taken over
    /// <see cref="IntegrationJob.StatsRect"/> when set, else whole-frame.
    /// Mirrors the historical chunked-path behaviour.</summary>
    private static Image NormaliseIfRequested(Image warped, IntegrationJob job)
    {
        if (!job.Options.ApplyNormalization) return warped;
        var stats = job.StatsRect.Width > 0 && job.StatsRect.Height > 0
            ? Normalizer.ComputeStats(warped, job.StatsRect)
            : Normalizer.ComputeStats(warped);
        return Normalizer.Apply(warped, stats, job.Options.NormalizationTarget);
    }

    private static void AccumulateStats(Image frame, float[][,] sum, float[][,] sumSq, uint[,] count)
    {
        var (channels, w, h) = frame.Shape;
        for (var c = 0; c < channels; c++)
        {
            var srcCh = frame.GetChannelArray(c);
            var sumCh = sum[c];
            var sumSqCh = sumSq[c];
            // Count is channel-shared: a NaN at (y, x) in any one channel
            // typically means warp coverage missed there in ALL channels
            // (the bilinear sampler emits NaN per-channel uniformly). We
            // update count using channel 0 only and trust the symmetry.
            // sum and sumSq still get per-channel updates.
            var updateCount = c == 0;
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var v = srcCh[y, x];
                    if (!float.IsNaN(v))
                    {
                        sumCh[y, x] += v;
                        sumSqCh[y, x] += v * v;
                        if (updateCount) count[y, x]++;
                    }
                }
            });
        }
    }

    /// <summary>In-place transform of (sum, sumSq, count) into (mean, sd, count).
    /// sd uses the population formula sqrt(sumSq/n − (sum/n)²) clamped to 0
    /// when numerical error pushes the radicand slightly negative.</summary>
    private static void FinaliseMeanAndSd(float[][,] sumToMean, float[][,] sumSqToSd, uint[,] count)
    {
        var channels = sumToMean.Length;
        var h = sumToMean[0].GetLength(0);
        var w = sumToMean[0].GetLength(1);
        for (var c = 0; c < channels; c++)
        {
            var meanCh = sumToMean[c];
            var sdCh = sumSqToSd[c];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var n = count[y, x];
                    if (n == 0)
                    {
                        meanCh[y, x] = float.NaN;
                        sdCh[y, x] = float.NaN;
                        continue;
                    }
                    var nF = (float)n;
                    var mean = meanCh[y, x] / nF;
                    var variance = sdCh[y, x] / nF - mean * mean;
                    meanCh[y, x] = mean;
                    sdCh[y, x] = variance > 0f ? MathF.Sqrt(variance) : 0f;
                }
            });
        }
    }

    /// <summary>Sigma-clip each frame pixel against the global (mean, sd).
    /// Returns the number of (pixel, channel) entries this frame contributed
    /// that were rejected. Updates combineSum + keptCount in place.</summary>
    private static long AccumulateClippedCombine(
        Image frame, float[][,] mean, float[][,] sd, float kappaLow, float kappaHigh,
        float[][,] combineSum, uint[,] keptCount)
    {
        var (channels, w, h) = frame.Shape;
        long localRejected = 0;
        for (var c = 0; c < channels; c++)
        {
            var srcCh = frame.GetChannelArray(c);
            var meanCh = mean[c];
            var sdCh = sd[c];
            var combineCh = combineSum[c];
            var updateCount = c == 0;
            Parallel.For(0, h,
                () => 0L,
                (y, _, localRej) =>
                {
                    for (var x = 0; x < w; x++)
                    {
                        var v = srcCh[y, x];
                        if (float.IsNaN(v)) continue;
                        var m = meanCh[y, x];
                        var s = sdCh[y, x];
                        if (float.IsNaN(m) || float.IsNaN(s))
                        {
                            // Per-pixel column was all-NaN in pass A; pass B
                            // can't tighten that, just include any non-NaN
                            // value we see now so the master isn't all-NaN.
                        }
                        else
                        {
                            var delta = v - m;
                            // Asymmetric low/high gates so the rejector matches the
                            // SigmaClipRejector knobs the user already configured.
                            if (delta < -kappaLow * s || delta > kappaHigh * s)
                            {
                                localRej++;
                                continue;
                            }
                        }
                        combineCh[y, x] += v;
                        if (updateCount) keptCount[y, x]++;
                    }
                    return localRej;
                },
                localRej => Interlocked.Add(ref localRejected, localRej));
        }
        return localRejected;
    }

    /// <summary>Extract the sigma-clip thresholds from the configured
    /// rejector. Falls back to (3.0, 3.0) when the rejector doesn't expose
    /// them or when no rejector is configured.</summary>
    private static (float Low, float High) ExtractKappa(IPixelRejector? rejector)
    {
        return rejector switch
        {
            SigmaClipRejector s => (s.LowSigma, s.HighSigma),
            WinsorizedSigmaClipRejector ws => (ws.LowSigma, ws.HighSigma),
            null => (3.0f, 3.0f),
            _ => (3.0f, 3.0f),
        };
    }
}
