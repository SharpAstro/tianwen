using System;
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

    [Fact]
    public void AComputerTooSmallForTheChosenStrategyRefusesInWords()
    {
        var nominal = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30).ToNominalHost();
        var selection = IntegrationStrategySelector.Pick(nominal);
        var tiny = Probe(ram: 1L << 20, freeRam: 1L << 20, disk: 1L << 20);

        var ex = Should.Throw<InvalidOperationException>(() => StackingPipeline.RequireThisComputerFits(selection, tiny, 100, stated: false));
        ex.Message.ShouldContain("The stack chose");
        ex.Message.ShouldContain("cannot run it");
    }

    [Fact]
    public void AStatedStrategyThatDoesNotFitIsRefusedAsStated()
    {
        var nominal = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30).ToNominalHost();
        var selection = IntegrationStrategySelector.Pick(nominal);
        var tiny = Probe(ram: 1L << 20, freeRam: 1L << 20, disk: 1L << 20);

        var ex = Should.Throw<InvalidOperationException>(() => StackingPipeline.RequireThisComputerFits(selection, tiny, 100, stated: true));
        ex.Message.ShouldContain("you stated");
        ex.Message.ShouldNotContain("The stack chose");
    }

    [Fact]
    public void AComputerThatFitsIsNotRefused()
    {
        var nominal = Probe(ram: 8L << 30, freeRam: 2L << 30, disk: 50L << 30).ToNominalHost();
        var selection = IntegrationStrategySelector.Pick(nominal);

        StackingPipeline.RequireThisComputerFits(selection, nominal, 100, stated: false);
    }
}
