using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Last-resort strategy when no other fits: split the N frames into K chunks,
/// integrate each chunk to a partial (value, weight) master, combine the K
/// partials with weighted mean. Pays a fidelity cost because per-chunk
/// rejection is not equivalent to across-all-frames rejection -- a pixel
/// that's an outlier vs the full distribution may not be one within its
/// chunk. Documented in PLAN-stacking.md:181-187.
/// </summary>
public sealed class ChunkedTwoPassStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;
    private readonly int _minChunkSize;

    /// <param name="minChunkSize">Smallest frame-count per chunk we accept;
    /// below this rejection is too noisy to be useful. SigmaClipRejector
    /// needs at least 4 entries for the (mean, sigma) pair to mean
    /// anything.</param>
    public ChunkedTwoPassStrategy(IntegrationCostModel? costs = null, int minChunkSize = 8)
    {
        _costs = costs ?? new IntegrationCostModel();
        _minChunkSize = minChunkSize;
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.ChunkedTwoPass;

    /// <summary>Significantly below the others: rejection fidelity degrades
    /// with chunking, partials carry quantisation, and the final combine
    /// reintroduces per-pixel noise.</summary>
    public double FidelityScore => 0.80;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        var ramCap = budget.AllowedRam(probe);
        // Chunk size = how many frames we can hold in RAM simultaneously,
        // minus the output canvas + one running partial.
        var perChunkBudget = ramCap - probe.OutputRamBytes - probe.CanvasBytes;
        if (perChunkBudget <= 0)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: probe.OutputRamBytes + probe.CanvasBytes,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"output ({Format.GB(probe.OutputRamBytes)}) + partial ({Format.GB(probe.CanvasBytes)}) bust RAM cap {Format.GB(ramCap)}");
        }

        var chunkSize = (int)Math.Min(probe.FrameCount, perChunkBudget / probe.FrameBytes);
        if (chunkSize < _minChunkSize)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: probe.OutputRamBytes + probe.CanvasBytes + (long)_minChunkSize * probe.FrameBytes,
                EstimatedDiskBytes: 0,
                EstimatedDuration: _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe),
                Rationale: $"chunk size floor {_minChunkSize} frames would need {Format.GB(probe.FrameBytes * _minChunkSize)}, only {Format.GB(perChunkBudget)} free");
        }

        var k = (probe.FrameCount + chunkSize - 1) / chunkSize;
        var ram = (long)chunkSize * probe.FrameBytes + probe.CanvasBytes + probe.OutputRamBytes;

        // Two passes worth of compute: per-chunk integrate + final combine.
        // The final combine touches K canvases worth of pixels, cheap vs the
        // per-chunk integrate, so approximate as 1.0× WarpAllFrames + StackAllFrames.
        // Each chunk decodes + debayers its frames once -- across all chunks
        // that's still 1.0× DebayerAllFrames total.
        var eta = _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe);

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"{k} chunks of {chunkSize}, {Format.GB(ram)} / {Format.GB(ramCap)} (rejection fidelity degraded)");
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        // Snapshot RAM at RunAsync time (may have changed since Pick) so the
        // chunk size accounts for current pressure rather than probe-time.
        var info = GC.GetGCMemoryInfo();
        var freeRam = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var ramCap = (long)(freeRam * new ResourceBudget().RamSafetyFactor);

        // Producer doesn't carry shape info; cache it from the first frame and
        // size the accumulator buffers from there. Chunk size is recomputed
        // from frame bytes once we know them.
        var combiner = job.Options.Combiner ?? new MeanCombiner();
        var rejector = job.Options.Rejector;

        var buffer = new List<Image>();
        float[][,]? sumValues = null;   // [channel][h, w] -- per-channel weighted sum
        float[,]? sumWeights = null;    // [h, w] -- channel-shared weight, since the
                                        // rejector RejectionMap is single-channel mean.
        var totalFrames = 0;
        var chunkSize = 0;
        long totalRejections = 0;
        Image? firstFrame = null;

        void FlushChunk()
        {
            if (buffer.Count == 0) return;
            ct.ThrowIfCancellationRequested();
            // Pure reject+combine: frames are already normalised. ApplyNormalization
            // off so Integrator doesn't re-compute (would use whole-frame stats and
            // double-normalise).
            var chunkResult = Integrator.Integrate(
                buffer,
                new IntegrationOptions(
                    Rejector: rejector,
                    Combiner: combiner,
                    ApplyNormalization: false));

            var (chans, w, h) = chunkResult.Master.Shape;
            sumValues ??= AllocSum(chans, h, w);
            sumWeights ??= new float[h, w];

            var rejectArr = chunkResult.RejectionMap.GetChannelArray(0);
            var n_k = buffer.Count;

            // Per-pixel weight = n_k * (1 - rejection_fraction). Channel-shared
            // because RejectionMap is the mean across channels.
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    sumWeights[y, x] += n_k * (1f - rejectArr[y, x]);
                }
            });

            // Per-channel weighted sum.
            for (var c = 0; c < chans; c++)
            {
                var sumArr = sumValues[c];
                var masterArr = chunkResult.Master.GetChannelArray(c);
                Parallel.For(0, h, y =>
                {
                    for (var x = 0; x < w; x++)
                    {
                        var weight = n_k * (1f - rejectArr[y, x]);
                        sumArr[y, x] += masterArr[y, x] * weight;
                    }
                });
            }

            totalRejections += chunkResult.TotalRejections;
            buffer.Clear();
        }

        await foreach (var warped in job.WarpedFrames(ct).WithCancellation(ct))
        {
            firstFrame ??= warped;
            if (chunkSize == 0)
            {
                // Size chunks at RunAsync time using actual first-frame bytes;
                // matches the evaluate-time math but uses live frame shape.
                var (chans, w, h) = warped.Shape;
                var frameBytes = (long)w * h * chans * sizeof(float);
                var canvasBytes = frameBytes;
                var outputRamBytes = canvasBytes * (job.Options.Rejector is not null ? 2 : 1);
                var perChunkBudget = ramCap - outputRamBytes - canvasBytes; // 1 partial canvas + output
                if (perChunkBudget <= 0)
                {
                    throw new InvalidOperationException(
                        $"ChunkedTwoPass: RAM cap {ramCap / 1e9:F2} GB insufficient for output + partial canvas.");
                }
                chunkSize = (int)Math.Max(_minChunkSize, perChunkBudget / frameBytes);
                chunkSize = Math.Min(chunkSize, job.ExpectedFrameCount);
            }

            // Pre-normalise the warped frame using StatsRect-aware stats so
            // the chunk's Integrator only does reject+combine.
            var stats = job.StatsRect.Width > 0 && job.StatsRect.Height > 0
                ? Normalizer.ComputeStats(warped, job.StatsRect)
                : Normalizer.ComputeStats(warped);
            var normalised = Normalizer.Apply(warped, stats, job.Options.NormalizationTarget);
            buffer.Add(normalised);
            totalFrames++;

            if (buffer.Count >= chunkSize) FlushChunk();
        }
        FlushChunk();

        if (sumValues is null || sumWeights is null || firstFrame is null || totalFrames == 0)
        {
            throw new InvalidOperationException("ChunkedTwoPass: producer yielded no frames");
        }

        // Finalise: divide per-channel sums by weights, derive rejection map
        // from the weight deficit relative to N.
        var (channels, width, height) = firstFrame.Shape;
        var masterData = Image.CreateChannelData(channels, height, width);
        var rejectMapData = Image.CreateChannelData(1, height, width);
        var rejectMap = rejectMapData[0];

        for (var c = 0; c < channels; c++)
        {
            var masterArr = masterData[c];
            var sumArr = sumValues[c];
            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var w = sumWeights[y, x];
                    masterArr[y, x] = w > 0 ? sumArr[y, x] / w : 0f;
                }
            });
        }
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                rejectMap[y, x] = totalFrames > 0
                    ? Math.Max(0f, 1f - sumWeights[y, x] / totalFrames)
                    : 0f;
            }
        });

        var meanRate = totalFrames > 0
            ? (double)totalRejections / ((double)totalFrames * width * height * channels)
            : 0.0;

        var masterImage = new Image(
            data: masterData, bitDepth: BitDepth.Float32,
            maxValue: firstFrame.MaxValue, minValue: 0f,
            pedestal: firstFrame.Pedestal, imageMeta: firstFrame.ImageMeta);
        var rejectMapImage = new Image(
            data: rejectMapData, bitDepth: BitDepth.Float32,
            maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: firstFrame.ImageMeta);

        return new IntegrationResult(masterImage, rejectMapImage, totalFrames, totalRejections, meanRate);
    }

    private static float[][,] AllocSum(int channels, int h, int w)
    {
        var arr = new float[channels][,];
        for (var c = 0; c < channels; c++) arr[c] = new float[h, w];
        return arr;
    }
}
