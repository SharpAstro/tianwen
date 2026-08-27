using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Where star detection's FIRST pass starts (<see cref="Image.FirstPassDetectionLevel"/>): the
    /// histogram's star level, floored at 3.5 sigma and capped at 30.
    /// </summary>
    /// <remarks>
    /// <para><b>The case that made the cap necessary.</b> A 60 s M42 Ha sub (iTelescope 31, 3055x3056)
    /// measured <c>bg=199.7, noise=74, star_level=7177</c> -- the nebula fills the histogram's bright
    /// bins, so the level read off it was <b>97x the noise</b>. The first pass therefore ran at 94 sigma
    /// and accepted <b>8</b> stars from 1449 analysed candidates, where ASTAP found <b>40</b> in the same
    /// frame; the plate solve then missed pair-lock by one hit (9 against 10) with 63 catalogue anchors
    /// available, so the catalogue was never the constraint. The retry ladder (30 sigma, then 7) exists
    /// for exactly this and could not run: <c>CatalogPlateSolver</c> pins <c>maxRetries: 0</c> to keep a
    /// polar-align rung inside its 5.5 s budget.</para>
    /// <para><b>Why this is an arithmetic test and not a frame test.</b> The failure is two scalars, and
    /// a faithful fixture is an 18 MB sub that cannot be embedded. A synthetic nebula does NOT reproduce
    /// it: measured, a 200-sigma synthetic blob inflates the <i>noise</i> estimate 11x (0.003 -> 0.033)
    /// so the 3.5-sigma floor alone suppresses the stars, and <c>star_level/noise</c> lands at 8 rather
    /// than 97 -- the cap is never even reached. Testing the arithmetic against the real numbers says
    /// something; testing a synthetic frame would have said something else and looked like the same
    /// thing.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class FirstPassDetectionLevelTests
    {
        /// <summary>The real M42 Ha numbers: 7177 must come down to the ladder's top rung, 30 x 74.</summary>
        [Fact]
        public void TheNebulaFrameStartsAtTheLaddersTopRungRatherThanAt94Sigma()
        {
            Image.FirstPassDetectionLevel(noiseLevel: 74f, starLevel: 7177f, maxNoiseSigma: Image.MaxFirstPassNoiseSigma)
                .ShouldBe(2220f);
        }

        [Theory]
        // An ordinary star field: the histogram level sits well inside the cap, so it is used verbatim
        // and this path is arithmetically identical to the pre-cap behaviour. Both rows are measured
        // from the synthetic field the probe built (star_level/noise 19.1 and 18.8).
        [InlineData(0.003f, 0.0573f, 0.0573f)]
        [InlineData(0.0033f, 0.0621f, 0.0621f)]
        // Exactly at the cap: still verbatim, so the boundary is inclusive and 30 sigma is reachable.
        [InlineData(10f, 300f, 300f)]
        // Past it by a hair: capped. The cap must not be a wide dead zone that quietly rounds levels.
        [InlineData(10f, 301f, 300f)]
        // A very short exposure, where the histogram sees no bright tail at all: the 3.5-sigma FLOOR
        // decides, and the cap must not lift a level the floor already raised.
        [InlineData(10f, 5f, 35f)]
        [InlineData(10f, 0f, 35f)]
        public void TheLevelIsTheStarLevelFlooredAt3Point5SigmaAndCappedAt30(
            float noise, float starLevel, float expected)
        {
            Image.FirstPassDetectionLevel(noise, starLevel, Image.MaxFirstPassNoiseSigma).ShouldBe(expected, 1e-4f);
        }

        /// <summary>
        /// The cap is stated in noise sigmas, and 30 is not a fresh constant: it is the top rung the
        /// retry ladder itself steps down from (30 -> 7 -> stop). If someone changes one, this fails and
        /// points at the other, because a cap above the ladder's first retry would leave a frame worse
        /// off on pass 1 than the pass that follows it.
        /// </summary>
        [Fact]
        public void TheCapIsTheRetryLaddersOwnTopRung()
        {
            Image.MaxFirstPassNoiseSigma.ShouldBe(30f);
        }
    }
}
