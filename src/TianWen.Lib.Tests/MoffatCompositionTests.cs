using Shouldly;
using System;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <see cref="MoffatComposition"/> against answers known in advance. The class exists because a
    /// quadrature of FWHMs over-reads a Moffat difference kernel by a quarter (E1b), so the tests pin
    /// the Gaussian limit where quadrature IS right, the round trip, and the size of the quadrature
    /// error on the archive's own numbers.
    /// </summary>
    public class MoffatCompositionTests
    {
        [Fact]
        public void InTheGaussianLimitTheComposedWidthIsTheQuadratureSum()
        {
            // A Moffat with a very large beta is a Gaussian, and two Gaussians compose in quadrature.
            var composed = MoffatComposition.ComposedFwhm(2.15, 1e4, 2.0, 1e4);
            composed.ShouldBe(Math.Sqrt((2.15 * 2.15) + (2.0 * 2.0)), tolerance: 0.05);
        }

        [Fact]
        public void ComposingWithANearDeltaLeavesTheWidthAlone()
        {
            MoffatComposition.ComposedFwhm(2.8, 5.0, 0.1, 4.0).ShouldBe(2.8, tolerance: 0.03);
        }

        [Theory]
        [InlineData(2.15, 5.0, 2.0, 4.0)]
        [InlineData(2.15, 3.0, 1.0, 4.0)]
        [InlineData(3.7, 8.0, 3.0, 4.0)]
        public void DifferenceFwhmInvertsComposedFwhm(double cleanFwhm, double cleanBeta, double kernelFwhm, double kernelBeta)
        {
            var observed = MoffatComposition.ComposedFwhm(cleanFwhm, cleanBeta, kernelFwhm, kernelBeta);
            var recovered = MoffatComposition.DifferenceFwhm(cleanFwhm, cleanBeta, observed, kernelBeta);
            recovered.ShouldBe(kernelFwhm, tolerance: 0.03);
        }

        [Fact]
        public void QuadratureOverReadsAMoffatKernelByAboutAQuarterOnTheArchivesCores()
        {
            // E1b's 1.3-1.6x band: a 2 px beta-4 kernel on a 2.15 px beta-5 core reads 1.24x its width
            // through sqrt(obs^2 - clean^2). Pinned as a range rather than a point, since the grid step
            // moves the third decimal.
            var observed = MoffatComposition.ComposedFwhm(2.15, 5.0, 2.0, 4.0);
            var quadrature = Math.Sqrt((observed * observed) - (2.15 * 2.15));
            (quadrature / 2.0).ShouldBeInRange(1.15, 1.30);
            MoffatComposition.DifferenceFwhm(2.15, 5.0, observed, 4.0).ShouldBe(2.0, tolerance: 0.03);
        }

        [Fact]
        public void AnObservedWidthNoWiderThanTheCleanOneIsNoBlurNotZero()
        {
            double.IsNaN(MoffatComposition.DifferenceFwhm(2.15, 5.0, 2.15, 4.0)).ShouldBeTrue();
            double.IsNaN(MoffatComposition.DifferenceFwhm(2.15, 5.0, 2.0, 4.0)).ShouldBeTrue();
        }
    }
}
