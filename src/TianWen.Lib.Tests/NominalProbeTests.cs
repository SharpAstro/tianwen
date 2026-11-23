using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>A workbench step chooses its strategy against a fixed nominal host (#2467), so its key holds one master everywhere.</summary>
public sealed class NominalProbeTests
{
    private static IntegrationProbe Probe(long ram, long freeRam, long disk) => new(
        FrameCount: 100, FrameWidth: 4000, FrameHeight: 3000, ChannelCount: 3, CanvasWidth: 4000, CanvasHeight: 3000,
        AvailableRamBytes: ram, AvailableDiskBytes: disk, StagingDir: "staging", SensorType: SensorType.Monochrome,
        StagingDiskKind: DiskKind.Hdd, FreeRamBytes: freeRam);

    [Fact]
    public void TwoComputersAreOneNominalHostAndPickTheSameStrategy()
    {
        var small = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30);
        var large = Probe(ram: 128L << 30, freeRam: 100L << 30, disk: 4L << 40);

        small.ToNominalHost().ShouldBe(large.ToNominalHost());
        var pickSmall = IntegrationStrategySelector.Pick(small.ToNominalHost());
        var pickLarge = IntegrationStrategySelector.Pick(large.ToNominalHost());

        pickSmall.Chosen.Kind.ShouldBe(pickLarge.Chosen.Kind);
        pickSmall.Sink.ShouldBe(pickLarge.Sink);
    }

    // 100 frames of 4000 x 3000 x 3 floats are 14 GB: nothing the nominal host picks for them runs in 1 GB of memory
    // and 1 GB of disk, while the nominal host itself runs what it picked.
    private static IntegrationProbe Tiny() => Probe(ram: 1L << 30, freeRam: 1L << 30, disk: 1L << 30);

    [Fact]
    public void AComputerThatCannotRunTheNominalPickIsRefusedInWordsAndOneThatCanIsNot()
    {
        var job = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30);
        var selection = IntegrationStrategySelector.Pick(job.ToNominalHost());

        var refusal = IntegrationStrategySelector.NominalHostRefusal(selection, Tiny(), frames: 100, stated: null);

        refusal.ShouldNotBeNull().ShouldStartWith($"The stack chose {selection.Chosen.Kind} for 100 frames, and this computer cannot run it: ");
        IntegrationStrategySelector.NominalHostRefusal(selection, job.ToNominalHost(), frames: 100, stated: null).ShouldBeNull();
    }

    [Fact]
    public void AStatedStrategyThatDoesNotFitIsRefusedAsThePersonsOwn()
    {
        var job = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30);
        var selection = IntegrationStrategySelector.Pick(job.ToNominalHost(), preferred: IntegrationStrategyKind.InRamAllFrames);
        selection.Chosen.Kind.ShouldBe(IntegrationStrategyKind.InRamAllFrames);

        var refusal = IntegrationStrategySelector.NominalHostRefusal(selection, Tiny(), frames: 100, stated: IntegrationStrategyKind.InRamAllFrames);

        refusal.ShouldNotBeNull().ShouldStartWith("The strategy InRamAllFrames you stated for 100 frames does not fit this computer: ");
        refusal.ShouldNotContain("The stack chose");
    }
}
