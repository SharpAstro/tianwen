using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Highest-fidelity strategy: warp every frame into memory, run the
/// in-memory <see cref="Integrator"/> across all N at once. No disk; peak
/// RAM is <c>N * frameBytes + outputBytes</c>. Only viable for small stacks
/// or roomy hosts -- 244 frames at 3008^2 RGB is 27 GB.
/// </summary>
public sealed class InRamAllFramesStrategy : IIntegrationStrategy
{
    private readonly IntegrationCostModel _costs;

    public InRamAllFramesStrategy(IntegrationCostModel? costs = null)
    {
        _costs = costs ?? new IntegrationCostModel();
    }

    public IntegrationStrategyKind Kind => IntegrationStrategyKind.InRamAllFrames;

    /// <summary>Reference quality: every frame contributes to the rejector at
    /// full float32 precision in a single pass.</summary>
    public double FidelityScore => 1.00;

    public bool SupportsLiveStacking => false;

    public StrategyFit Evaluate(IntegrationProbe probe, ResourceBudget budget)
    {
        var ram = probe.AllFramesRamBytes + probe.OutputRamBytes;
        var cap = budget.AllowedRam(probe);
        var eta = _costs.LoadAndCalibrateAllFrames(probe) + _costs.DebayerAllFrames(probe) + _costs.WarpAllFrames(probe) + _costs.StackAllFrames(probe);

        if (ram > cap)
        {
            return new StrategyFit(
                CanRun: false,
                EstimatedRamBytes: ram,
                EstimatedDiskBytes: 0,
                EstimatedDuration: eta,
                Rationale: $"needs {Format.GB(ram)} RAM, cap {Format.GB(cap)}");
        }

        return new StrategyFit(
            CanRun: true,
            EstimatedRamBytes: ram,
            EstimatedDiskBytes: 0,
            EstimatedDuration: eta,
            Rationale: $"all {probe.FrameCount} frames + output fit in RAM ({Format.GB(ram)} / {Format.GB(cap)})");
    }

    public async ValueTask<IntegrationResult> RunAsync(IntegrationJob job, CancellationToken ct)
    {
        // Collect every warped frame into a list, then hand off to the
        // in-memory Integrator. ApplyNormalization stays on -- the Integrator
        // computes per-frame stats with whole-frame stats by default.
        // For better stats-rect honesty on heavy-motion stacks the user should
        // pick FootprintStaged; InRam is only the right choice when the stack
        // is small enough that the NaN-edge fraction is negligible.
        var frames = new List<Image>(job.ExpectedFrameCount);
        await foreach (var frame in job.WarpedFrames(ct).WithCancellation(ct))
        {
            frames.Add(frame);
        }

        return Integrator.Integrate(frames, job.Options);
    }
}

internal static class Format
{
    public static string GB(long bytes) => $"{bytes / 1e9:F2} GB";
    public static string MB(long bytes) => $"{bytes / 1e6:F1} MB";
}
