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

        [Fact]
        public void AnInfiniteBetaIsTheGaussianProfile()
        {
            MoffatComposition.Profile(2.0, double.PositiveInfinity, 0.0).ShouldBe(1.0, tolerance: 1e-12);
            MoffatComposition.Profile(2.0, double.PositiveInfinity, 1.0).ShouldBe(0.5, tolerance: 1e-9);
            MoffatComposition.Profile(3.0, double.PositiveInfinity, 1.5).ShouldBe(0.5, tolerance: 1e-9);
        }

        /// <summary>
        /// E1d's finding: PsfKernel samples at pixel centres, so a kernel under about 1.5 px blurs by less
        /// than its label. The expected values are the numeric composition of the point-sampled kernel with
        /// a beta-3 core (deconvolver-training.md, E1d): 0.73 px for a nominal 1 px on a 2.15 px core, a
        /// near-delta for a nominal 0.5 px, and the label itself from 2 px up.
        /// </summary>
        [Theory]
        [InlineData(1.0, 0.73, 0.08)]
        [InlineData(0.5, 0.12, 0.08)]
        [InlineData(2.0, 2.0, 0.10)]
        [InlineData(3.0, 3.0, 0.10)]
        public void APointSampledKernelUnderTwoPixelsIsNarrowerThanItsLabel(double nominal, double expectedEffective, double tolerance)
        {
            var kernel = PsfKernel.Moffat(nominal, 4.0);
            var effective = MoffatComposition.EffectiveKernelFwhm(kernel, 2.15, 3.0);
            effective.ShouldBe(expectedEffective, tolerance);
        }

        [Fact]
        public void TheDiscreteCompositionAgreesWithTheContinuousOneOnceTheKernelIsSampledDensely()
        {
            // At 3 px the kernel has a dozen samples across its FWHM; the two compositions should meet.
            var kernel = PsfKernel.Moffat(3.0, 4.0);
            var discrete = MoffatComposition.ComposedFwhm(2.15, 3.0, kernel);
            var continuous = MoffatComposition.ComposedFwhm(2.15, 3.0, 3.0, 4.0);
            discrete.ShouldBe(continuous, tolerance: 0.06);
        }

        [Fact]
        public void AGaussianKernelHasAnEffectiveWidthNearItsLabelWhenWide()
        {
            // Truncated at three sigma and renormalised, so a little under the label, never over.
            var effective = MoffatComposition.EffectiveKernelFwhm(PsfKernel.Gaussian(3.0), 2.15, 3.0);
            effective.ShouldBeInRange(2.8, 3.05);
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
