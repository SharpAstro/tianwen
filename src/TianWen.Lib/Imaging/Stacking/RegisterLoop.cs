using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>
    /// The shared per-frame register loop body: min-stars floor, quad-form, tolerance-ladder match,
    /// rigid refine -- plus the bookkeeping every caller was duplicating around it (the skip
    /// counters and the <see cref="RegistrationCensus"/> input lists, in call order, which is what
    /// lets the census report a TREND).
    ///
    /// <para><b>What deliberately stays at the call site:</b> logging (the two consumers log at
    /// different levels with different identities: the dataset registrar per session at Debug, the
    /// stacking pipeline per file at Information) and result collection (a
    /// <c>RegisteredSub</c>-to-be vs a matched tuple feeding a quality filter). This class answers
    /// "did this frame register, and what happened"; what that means is the caller's business.</para>
    ///
    /// <para><b>Works entirely from star lists.</b> No overload takes an <see cref="Image"/>: by
    /// the time a frame reaches the register loop its stars are already detected (the measure pass
    /// needs them for the reference score), so registering is centroid work and reading pixels here
    /// would be the double-detect this extraction removed from the stacking pipeline.</para>
    ///
    /// <para>Every frame the loop attempts contributes stars/HFD/ellipticity to the census,
    /// including the reference (via <see cref="AddReference"/>: it is one of the frames whose focus
    /// the census describes, and excluding it would silently drop the sharpest sample from the
    /// spread). Quad counts are contributed only by frames that got as far as quad-forming, which
    /// is what <see cref="RegistrationCensus.Spread.QuadFrames"/> counts.</para>
    /// </summary>
    public sealed class RegisterLoop : IDisposable
    {
        /// <summary>Why a frame did not register. <see cref="None"/> means it matched.</summary>
        public enum SkipCause
        {
            None,
            TooFewStars,
            NoQuadFit,
        }

        /// <summary>
        /// One frame's trip through the loop. <paramref name="Transform"/> is the REFINED
        /// reference-space transform (rigid refinement applied), null when skipped. The match and
        /// refine diagnostics carry NaN / zero on the paths that never reached them.
        /// </summary>
        public readonly record struct Attempt(
            Matrix3x2? Transform,
            SkipCause Skip,
            int LightQuads,
            float QuadTolerance,
            float MatchRmsPx,
            float RefineScale,
            float RefineRotationDeg,
            float RefineTx,
            float RefineTy,
            float RefineRmsPx,
            int RefineMatchedPairs);

        private readonly SortedStarList _referenceSorted;
        private readonly int _quadStars;
        private readonly List<int> _censusStars = [];
        private readonly List<int> _censusQuads = [];
        private readonly List<float> _censusHfd = [];
        private readonly List<float> _censusEcc = [];

        private RegisterLoop(SortedStarList referenceSorted, int referenceQuadCount, int quadStars)
        {
            _referenceSorted = referenceSorted;
            ReferenceQuadCount = referenceQuadCount;
            _quadStars = quadStars;
        }

        /// <summary>
        /// Builds the loop around the reference's star list: sorts it and forms its quads up front,
        /// so the counts are reportable before the first frame and the per-frame matcher reuses the
        /// memoised quad build.
        /// </summary>
        public static async Task<RegisterLoop> CreateAsync(StarList referenceStars, int quadStars, CancellationToken cancellationToken = default)
        {
            var referenceSorted = new SortedStarList(referenceStars);
            var referenceQuads = await referenceSorted.FindQuadsAsync(maxStars: quadStars, cancellationToken: cancellationToken);
            return new RegisterLoop(referenceSorted, referenceQuads.Count, quadStars);
        }

        /// <summary>Stars in the reference's sorted list (the "reference stars=" both consumers log).</summary>
        public int ReferenceStarCount => _referenceSorted.Count;

        /// <summary>Quads formed from the reference's top-K stars.</summary>
        public int ReferenceQuadCount { get; }

        /// <summary>Frames skipped under <see cref="FrameRegistration.MinStarsForMatch"/>.</summary>
        public int SkippedTooFewStars { get; private set; }

        /// <summary>Frames whose quads matched at no rung of the tolerance ladder.</summary>
        public int SkippedNoQuadFit { get; private set; }

        /// <summary>
        /// Contributes the reference frame to the census without registering it (its transform is
        /// identity by definition, so it never goes through <see cref="RegisterAsync"/>).
        /// </summary>
        public void AddReference(in FrameMetrics referenceMetrics)
        {
            _censusStars.Add(referenceMetrics.StarCount);
            _censusHfd.Add(referenceMetrics.MedianHfd);
            _censusEcc.Add(referenceMetrics.MedianEllipticity);
        }

        /// <summary>Registers one frame's detected stars against the reference.</summary>
        public async Task<Attempt> RegisterAsync(StarList stars, FrameMetrics metrics, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _censusStars.Add(stars.Count);
            _censusHfd.Add(metrics.MedianHfd);
            _censusEcc.Add(metrics.MedianEllipticity);
            if (stars.Count < FrameRegistration.MinStarsForMatch)
            {
                SkippedTooFewStars++;
                return new Attempt(null, SkipCause.TooFewStars, 0, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, 0);
            }
            using var lightSorted = new SortedStarList(stars);
            var lightQuads = await lightSorted.FindQuadsAsync(maxStars: _quadStars, cancellationToken: cancellationToken);
            _censusQuads.Add(lightQuads.Count);
            var (solution, quadTolerance, matchRmsPx) = await FrameRegistration.TryMatchAsync(lightSorted, _referenceSorted, _quadStars);
            if (solution is null)
            {
                SkippedNoQuadFit++;
                return new Attempt(null, SkipCause.NoQuadFit, lightQuads.Count, quadTolerance, matchRmsPx, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, 0);
            }
            // Rigid (rotation + isotropic scale + translation) refinement on top of the bulk quad
            // fit -- closes the sub-pixel residual the fingerprint match averages away, which
            // drizzle would otherwise preserve as a "dumbbell" stretch on every star. Essentially
            // free (~1 ms per frame, brute-force NN over ~100 stars), so always applied.
            var (refined, scale, rotationDeg, tx, ty, refineRmsPx, matchedPairs) =
                RegistrationRefiner.RefineRigid(lightSorted, _referenceSorted, solution.Value);
            return new Attempt(refined, SkipCause.None, lightQuads.Count, quadTolerance, matchRmsPx, scale, rotationDeg, tx, ty, refineRmsPx, matchedPairs);
        }

        /// <summary>The census over every frame this loop saw, in the order it saw them.</summary>
        public RegistrationCensus.Spread? MeasureCensus()
            => RegistrationCensus.Measure(_censusStars, _censusQuads, _censusHfd, _censusEcc);

        public void Dispose() => _referenceSorted.Dispose();
    }
}
