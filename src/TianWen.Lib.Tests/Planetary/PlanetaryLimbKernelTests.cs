using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The limb's kernel with a wing that can widen (docs/plans/planetary-restoration.md, R7 part 3): a disk blurred by a Gaussian core
/// convolved with the scatter's wing is fitted back to that kernel's transfer, where two Gaussians with the wing's share bounded at a
/// half could not follow it.
/// </summary>
public class PlanetaryLimbKernelTests
{
    private const int Size = 224;

    [Fact]
    public void ADiskBlurredByACoreAndAWingIsFittedBackToItsTransfer()
    {
        var options = new LimbFitOptions(AxisRatio: 0.935);
        var geometry = new LimbFit(111.5, 112.2, 36, 90, 1.0, 1.0, 1, 0, 0, 0, 0, [], 0, true, NorthAngleDeg: 90);
        var sharp = PlanetaryLimbFit.SharpModel(geometry, options, Size, Size);
        var truth = new LimbKernel(CoreSigma: 1.1, WingFraction: 0.12, WingScale: 9, Brightness: 1, Sky: 0, RmsResidual: 0);
        var blurred = PlanetaryLimbKernel.Blur(sharp, Size, Size, truth);
        var random = new Random(4);
        var plane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            var noise = 0.003 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            plane[i / Size, i % Size] = (float)(0.05 + (0.8 * blurred[i]) + noise);
        }
        var stack = Image.FromChannel(plane);
        var fit = PlanetaryLimbKernel.Fit(stack, geometry with { PsfSigma = 1.6, Brightness = 0.8, Sky = 0.05 }, options, outerRadii: 1.8).ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"core {fit.CoreSigma:0.000} px, wing {fit.WingFraction:P1} of {fit.WingScale:0.00} px, brightness {fit.Brightness:0.000}, sky {fit.Sky:0.0000}");
        foreach (var f in new[] { 0.05, 0.1, 0.2, 0.3 })
        {
            fit.TransferAt(f).ShouldBe(truth.TransferAt(f), 0.02, $"at {f} cycles a pixel");
        }
    }
}
