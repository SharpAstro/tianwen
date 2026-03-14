using Shouldly;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class NeuralGuidePerformanceMonitorTests
{
    [Fact]
    public void GivenNoDataWhenQueryThenHelping()
    {
        var monitor = new NeuralGuidePerformanceMonitor();
        monitor.IsNeuralModelHelping.ShouldBeTrue("should give benefit of the doubt with no data");
    }

    [Fact]
    public void GivenNeuralBetterWhenQueryThenHelping()
    {
        var monitor = new NeuralGuidePerformanceMonitor { MinSamples = 5 };

        for (var i = 0; i < 10; i++)
        {
            // Neural error: 0.5, P-controller estimate: 1.0 → neural is better
            monitor.Record(i, actualError: 0.5, pControllerEstimatedResidual: 1.0);
        }

        monitor.IsNeuralModelHelping.ShouldBeTrue();
    }

    [Fact]
    public void GivenNeuralWorseWhenQueryThenNotHelping()
    {
        var monitor = new NeuralGuidePerformanceMonitor { MinSamples = 5 };

        for (var i = 0; i < 10; i++)
        {
            // Neural error: 2.0, P-controller estimate: 0.5 → neural is worse
            monitor.Record(i, actualError: 2.0, pControllerEstimatedResidual: 0.5);
        }

        monitor.IsNeuralModelHelping.ShouldBeFalse();
    }

    [Fact]
    public void GivenNeuralSlightlyWorseWhenWithinThresholdThenStillHelping()
    {
        var monitor = new NeuralGuidePerformanceMonitor { MinSamples = 5, ThresholdRatio = 1.15 };

        for (var i = 0; i < 10; i++)
        {
            // Neural 10% worse than P — within 15% threshold
            monitor.Record(i, actualError: 1.1, pControllerEstimatedResidual: 1.0);
        }

        monitor.IsNeuralModelHelping.ShouldBeTrue("10% worse is within 15% threshold");
    }

    [Fact]
    public void GivenResetWhenQueryThenHelping()
    {
        var monitor = new NeuralGuidePerformanceMonitor { MinSamples = 5 };

        for (var i = 0; i < 10; i++)
        {
            monitor.Record(i, actualError: 5.0, pControllerEstimatedResidual: 0.1);
        }

        monitor.IsNeuralModelHelping.ShouldBeFalse();

        monitor.Reset();
        monitor.IsNeuralModelHelping.ShouldBeTrue("should reset to benefit of the doubt");
    }
}
