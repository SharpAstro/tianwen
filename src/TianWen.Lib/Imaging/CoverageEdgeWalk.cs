using System;
using System.Buffers;
using TianWen.Lib.Geometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging
{
    /// <summary>Which side of a rectangle a walk comes in from.</summary>
    public enum CoverageEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>
    /// Knobs for <see cref="CoverageEdgeWalk"/>. Every default was measured rather than picked --
    /// see the class remarks for the corpus and the numbers.
    /// </summary>
    /// <remarks>
    /// A record CLASS, not a record struct: property initialisers on a record struct are skipped by
    /// <c>new()</c> and <c>default</c>, so a struct here would hand a caller a zero band thickness and
    /// a zero margin, which trims nothing and reads as the feature being absent.
    /// </remarks>
    public sealed record CoverageEdgeWalkOptions
    {
        public static CoverageEdgeWalkOptions Default { get; } = new CoverageEdgeWalkOptions();

        /// <summary>Band depth, in px, over which one profile sample is measured.</summary>
        public int BandThickness { get; init; } = 16;

        /// <summary>Tile length along the band. One sigma per tile, so a star, a trail or a nebula
        /// edge crossing part of a band moves its own tiles and not the answer.</summary>
        public int TileLength { get; init; } = 64;

        /// <summary>Profile sampling interval, in px, and therefore the resolution of the trim.</summary>
        public int Step { get; init; } = 4;

        /// <summary>How far in the settled reference is taken from, as a fraction of the perpendicular
        /// span. Samples between half this and this are pooled, so the reference is "just inside" --
        /// the same rows or columns as the band, which is what cancels a frame-wide noise gradient.
        ///
        /// <para>A FLOOR, not a fixed depth: the reference pool must begin outside the search window or
        /// it would be measured inside the very band being profiled, so the walk pushes it deeper when
        /// <see cref="SettleSearchFraction"/> asks for it. See <c>MeasureEdge</c>.</para></summary>
        public double ReferenceFraction { get; init; } = 0.15;

        /// <summary>The most one edge may lose, as a fraction of the perpendicular span.
        ///
        /// <para><b>This is the loss cap and nothing else.</b> It used to double as the depth by which
        /// the profile had to settle, which made the two inseparable: a dither strip deeper than the cap
        /// was not trimmed TO the cap, it was declined outright. Worse, turning the knob up to reach
        /// such a strip walked into the reference guard and switched the walk off silently, every edge
        /// of every master returning "nothing to trim" from 0.08 up. How far the walk LOOKS is now
        /// <see cref="SettleSearchFraction"/>.</para></summary>
        public double MaxTrimFraction { get; init; } = 0.05;

        /// <summary>How far in the walk probes for the profile to settle, as a fraction of the
        /// perpendicular span. Independent of <see cref="MaxTrimFraction"/>: a profile that settles
        /// between the cap and here is a real edge the caller is not willing to pay for, which is a
        /// different answer from a profile that never settles at all, and
        /// <see cref="CoverageEdgeTrim.SettleDepth"/> reports which.
        ///
        /// <para>0.10, twice the DEFAULT loss cap, so at the defaults the walk sees far enough to tell
        /// those two apart. It is a fixed value, not a multiple: a caller who raises the cap past it gets
        /// a search floored AT the cap (the walk never looks less far than it may trim), and has to raise
        /// this too to keep seeing past the cap. The floor, not a multiple, because the reference pool is
        /// pushed to twice the search, and a search of twice a large cap leaves no room for it.</para></summary>
        public double SettleSearchFraction { get; init; } = 0.10;

        /// <summary>How much further than <see cref="SettleSearchFraction"/> a second look goes, to
        /// CONFIRM that a depth is a border rather than the window.
        ///
        /// <para>The walk sees one window, and inside one window a real border and a gradient that
        /// happens to go quiet look identical: both report "flat from here inward to where I stopped".
        /// Looking further separates them. A border answers the same depth however far you look, because
        /// the quiet region beyond it really is quiet; a gradient answers deeper every time the window
        /// grows, because the answer WAS the window. Measured over 139 masters
        /// (<c>docs/plans/viewer-prerelease-fixes.md</c>): of 556 edges, 328 hold their depth and 81
        /// follow the window, and at the shipped window 10 of the 16 edges past the cap have no border
        /// at all, which is who a consumer acting on a bare depth was really acting on.</para>
        ///
        /// <para>1.5 rather than more because the reference pool is pushed to twice the widened search
        /// and has to stay inside the span. It costs one extra pass over a profile already computed, not
        /// a second profile: the wider profile is measured once and the settle rule is evaluated at both
        /// widths against the one reference level.</para></summary>
        public double ConfirmFactor { get; init; } = 1.5;

        /// <summary>How close to the settled level counts as settled.</summary>
        public double SettleMargin { get; init; } = 1.15;

        /// <summary>How much noisier than the settled level the outermost band must be before anything
        /// is trimmed at all. Below this the edge is indistinguishable from the interior and the walk
        /// reports nothing to do.</summary>
        public double MinimumRise { get; init; } = 1.15;

        /// <summary>Which percentile of the per-tile sigmas is the band's noise. The quietest tiles
        /// carry the least structure; p10 was the one that converged to 1.00x deep inside a real master
        /// where the median stayed at 1.25x.</summary>
        public int Percentile { get; init; } = 10;
    }

    /// <summary>Why an edge ended up with the depth it did. The outcome a caller reports, and the one
    /// thing a bare depth of zero could never say.</summary>
    public enum CoverageEdgeOutcome
    {
        /// <summary>The profile was flat from the outermost band in: there was no border to remove.</summary>
        Clean,

        /// <summary>The profile settled inside the loss cap, and <c>Depth</c> is where.</summary>
        Trimmed,

        /// <summary>The profile settled, but DEEPER than the loss cap allows. The edge is real and its
        /// extent is known (<c>SettleDepth</c>); the caller is simply not willing to pay for it, so
        /// <c>Depth</c> is zero. Raising <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/> past
        /// <c>SettleDepth</c> would trim it.</summary>
        BeyondCap,

        /// <summary>A depth was found, and looking further said it was not a border. The wider pass
        /// disagreed with the narrow one, so what the narrow window called settled was the window: this
        /// is a gradient the walk can now NAME rather than mistake for an edge with a known price.
        /// <c>SettleDepth</c> carries what was found, for diagnostics only; a consumer must not trim to
        /// it, which is the whole reason the outcome exists.</summary>
        Unconfirmed,

        /// <summary>The profile never settled inside the search window: the noise is still falling that
        /// far in, which is a property of the frame and not a border.</summary>
        NeverSettles,

        /// <summary>The walk could not measure this edge at all -- too few tiles along it, or no room
        /// for a reference pool outside the search window. NOT the same as <see cref="Clean"/>, and
        /// conflating the two is what let a mis-set option silently disable the whole feature.</summary>
        NotMeasurable,
    }

    /// <summary>What the walk found on one edge.</summary>
    /// <param name="Depth">Px to discard from that edge. Zero for every outcome but
    /// <see cref="CoverageEdgeOutcome.Trimmed"/>, so read <paramref name="Outcome"/> to know why.</param>
    /// <param name="Outcome">Why this edge got that depth.</param>
    /// <param name="EdgeRatio">The outermost band's noise over the settled level, so a caller can say
    /// how bad the edge was, or that it was fine. NaN when nothing could be measured.</param>
    /// <param name="SettleDepth">Where the profile settled, in px, whether or not the loss cap allowed
    /// trimming there. -1 when it never settled or could not be measured. This is what makes
    /// <see cref="CoverageEdgeOutcome.BeyondCap"/> actionable rather than a shrug.</param>
    public readonly record struct CoverageEdgeTrim(int Depth, CoverageEdgeOutcome Outcome, double EdgeRatio, int SettleDepth)
    {
        /// <summary>Whether the profile reached the settled level at all, at any depth: the PHYSICAL
        /// fact about the edge. True for <see cref="CoverageEdgeOutcome.BeyondCap"/>, which did settle,
        /// just past what the caller will pay. Not what a consumer with a fallback branches on; that is
        /// <see cref="Declined"/>.</summary>
        public bool Settled => Outcome is CoverageEdgeOutcome.Clean or CoverageEdgeOutcome.Trimmed or CoverageEdgeOutcome.BeyondCap;

        /// <summary>Whether the walk LEFT THE EDGE ALONE for a reason other than it being clean: the
        /// VERDICT the consumers act on. The viewer reports it as "edge held" and the CLI's
        /// <c>--trim-declined</c> trims such an edge by hand, and a band the cap refused
        /// (<see cref="CoverageEdgeOutcome.BeyondCap"/>) has to count here or both stop seeing the one
        /// edge they were written for. It reads the outcome, not <see cref="Settled"/>, because the two
        /// disagree on exactly that case.</summary>
        public bool Declined => Outcome is CoverageEdgeOutcome.BeyondCap or CoverageEdgeOutcome.Unconfirmed
            or CoverageEdgeOutcome.NeverSettles or CoverageEdgeOutcome.NotMeasurable;
    }

    /// <summary>
    /// What a consumer does with an edge the walk REFUSED. The verdict is the walk's; this is the
    /// policy on top of it, and it lives here because there is exactly one of it.
    /// </summary>
    /// <remarks>
    /// <para>It used to live in <c>ImageSubCommand</c>, on the argument that the viewer wants the
    /// opposite of what the CLI wants from the same verdict. That is an argument for a PARAMETER, not
    /// for a second implementation in another assembly, and the cost of getting it wrong was measured:
    /// with the policy in the CLI alone, <c>tianwen image autocrop</c> removed V1045 Ori's 356 px
    /// dither strip exactly while the stacking pipeline, which never had the policy, kept writing a
    /// master with the strip still on it. The fix worked on the path nobody looks at and not on the one
    /// that produced the reported bug.</para>
    ///
    /// <para>So the stacker, the CLI and the viewer all come through here, and the viewer differs only
    /// by asking for <see cref="KeepEveryPixel"/>.</para>
    /// </remarks>
    public sealed record CoverageTrimPolicy
    {
        /// <summary>Trim a refused edge as well: the default everywhere a master is being PRODUCED,
        /// because the ramp a crop keeps is exactly what a background model then fits.</summary>
        public static CoverageTrimPolicy Default { get; } = new CoverageTrimPolicy();

        /// <summary>Show every pixel that exists: a refused edge loses nothing. What a person looking
        /// at a frame wants, and wrong for anything feeding a fit.</summary>
        public static CoverageTrimPolicy KeepEveryPixel { get; } = new CoverageTrimPolicy { DeclinedFraction = 0.0 };

        /// <summary>What an edge with NO depth to read loses, as a fraction of the span. Zero leaves
        /// every refused edge alone. An edge whose depth was measured AND confirmed
        /// (<see cref="CoverageEdgeOutcome.BeyondCap"/>) ignores this and comes off at that depth
        /// instead; see <see cref="DepthFor"/>.</summary>
        public double DeclinedFraction { get; init; } = 0.05;

        /// <summary>Refuse a crop that would leave less than this on an axis.</summary>
        public int MinimumKeptPx { get; init; } = 16;

        /// <summary>What this edge actually loses.</summary>
        /// <remarks>
        /// A band past the cap was measured AND confirmed: the walk found the same depth looking
        /// further, so it is a border rather than a window, and that depth is what comes off with
        /// <see cref="DeclinedFraction"/> never consulted for it. Every other refusal falls back to the
        /// fraction, <see cref="CoverageEdgeOutcome.Unconfirmed"/> included: a depth that followed the
        /// window is not a border, and trimming to it would bite into a gradient on the strength of a
        /// number that only looks like an answer.
        /// </remarks>
        public int DepthFor(in CoverageEdgeTrim trim, int span)
        {
            if (!trim.Declined)
            {
                return trim.Depth;
            }

            if (DeclinedFraction <= 0)
            {
                return 0;
            }

            return trim.Outcome is CoverageEdgeOutcome.BeyondCap && trim.SettleDepth > 0
                ? trim.SettleDepth
                : (int)Math.Round(span * DeclinedFraction);
        }

        /// <summary>One edge's share of a log line, naming the px AND where the number came from,
        /// because a depth the walk measured and a fraction nobody measured are not the same claim.
        /// Null for an edge that did not move.</summary>
        public string? Describe(string name, in CoverageEdgeTrim trim, int depth) => (depth, trim.Outcome) switch
        {
            ( <= 0, _) => null,
            (_, CoverageEdgeOutcome.Trimmed) => $"{name} {depth} px",
            (_, CoverageEdgeOutcome.BeyondCap) => $"{name} {depth} px, settled there",
            (_, CoverageEdgeOutcome.Unconfirmed) => $"{name} {depth} px, blind (no border, the depth followed the window)",
            (_, CoverageEdgeOutcome.NeverSettles) => $"{name} {depth} px, blind (never settled)",
            _ => $"{name} {depth} px, blind (not measurable)",
        };
    }

    /// <summary>The four edges' verdicts, and the rectangle they leave.</summary>
    public readonly record struct CoverageEdgeTrims(
        CoverageEdgeTrim Left,
        CoverageEdgeTrim Top,
        CoverageEdgeTrim Right,
        CoverageEdgeTrim Bottom)
    {
        /// <summary>True when at least one edge was left alone for a reason other than being clean
        /// (<see cref="CoverageEdgeTrim.Declined"/>): refused, never settled, or settled past the cap.</summary>
        public bool AnyDeclined => Left.Declined || Top.Declined || Right.Declined || Bottom.Declined;

        /// <summary>Total px discarded across all four edges.</summary>
        public int TotalDepth => Left.Depth + Top.Depth + Right.Depth + Bottom.Depth;

        /// <summary>Applies the trims to the rectangle they were measured on, taking each edge's own
        /// answer and nothing more. A declined edge keeps its pixels. Equivalent to
        /// <c>Apply(rect, CoverageTrimPolicy.KeepEveryPixel)</c>, and kept because that IS the plain
        /// meaning of a set of trims.</summary>
        public PixelRect Apply(PixelRect rect) => Apply(rect, CoverageTrimPolicy.KeepEveryPixel);

        /// <summary>Applies the trims under a <paramref name="policy"/>, which decides the one thing a
        /// verdict does not: what an edge the walk REFUSED should lose.</summary>
        public PixelRect Apply(PixelRect rect, CoverageTrimPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var left = policy.DepthFor(Left, rect.Width);
            var right = policy.DepthFor(Right, rect.Width);
            var top = policy.DepthFor(Top, rect.Height);
            var bottom = policy.DepthFor(Bottom, rect.Height);
            var width = rect.Width - left - right;
            var height = rect.Height - top - bottom;
            return width > policy.MinimumKeptPx && height > policy.MinimumKeptPx
                ? new PixelRect(rect.X + left, rect.Y + top, width, height)
                : rect;
        }
    }

    /// <summary>
    /// Walks in from each edge of a rectangle until the local noise settles, and reports how much of
    /// each edge is under-exposed rather than merely present. This is the estimate for a frame we did
    /// NOT produce; a master of ours carries its own coverage plane, and
    /// <see cref="Image.LargestCoveredRectangle(Image, double)"/> is the exact answer, which wins
    /// wherever it exists.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem it addresses.</b> <see cref="Image.LargestCoveredRectangle()"/> finds where
    /// NO sub reached, so it answers "the largest rectangle inside the area at least one sub covered".
    /// A dithered stack has a further band inside that, with no zeros in it and real data, that fewer
    /// subs reached: partial coverage is not darker (row medians hold at 0.997 of the interior all the
    /// way to the edge), so only the noise gives it away.</para>
    /// <para><b>The reference is the profile's OWN level just inside, never the frame interior.</b>
    /// Measured on TianWen's own 10P Bayer-drizzle master, whose <c>.rejection.fits</c> sidecar is the
    /// accumulated per-pixel weight and therefore ground truth: the vertical-difference noise 24 px in
    /// from its left edge is 1.8x to 2.5x the centre's AT FULL COVERAGE -- every tile length, every
    /// percentile -- and it decays over about 460 px. A canvas edge is fed by fewer distinct dither
    /// phases, so its noise is less correlated and a difference-based sigma reads high there. Against
    /// the frame interior that reads as a 312 to 460 px band; the weight map says 4.</para>
    /// <para><b>A band whose end cannot be seen is not trimmed.</b> The rule is the SHALLOWEST depth
    /// from which every sample out to <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/> is within
    /// <see cref="CoverageEdgeWalkOptions.SettleMargin"/> of the settled level, so a profile still
    /// falling at that depth yields <see cref="CoverageEdgeTrim.Settled"/> false and no trim -- which is
    /// what saves the case above. Level matching against the interior, plateau matching against the
    /// edge's own deep median, and a knee/slope rule were all tried first, and all three trimmed 300+
    /// px of that master's left edge where the truth was 4.</para>
    /// <para><b>It is an estimate, and on a drizzled frame it can be blind.</b> Partial coverage pushes
    /// a difference-based sigma two ways at once -- fewer samples raise it, fewer drops per cell
    /// correlate the neighbours and lower it -- and on the same master's right edge those cancelled to
    /// 1.08x at 69% coverage. Use the coverage plane when there is one.</para>
    /// <para>Measured trims on the three real Astro Pixel Processor composites in the corpus, which
    /// carry no coverage plane: top 84 to 116 px, bottom 68 to 100, left 36 to 60, right 8 to 40,
    /// stable to about 20 px across margins 1.10 to 1.20.</para>
    /// <para>Cost is one pass over four bands per sample: ~5.5 s in the Python harness the constants
    /// were measured with (<c>tools/coverage-edge-walk/</c>), and it reads only the bands, never the
    /// whole frame. Like <see cref="Image.LargestCoveredRectangle()"/> it belongs behind an action.</para>
    /// </remarks>
    public static class CoverageEdgeWalk
    {
        /// <summary>The rectangle left once every edge's under-exposed band is discarded.</summary>
        /// <remarks>
        /// <paramref name="policy"/> decides what a REFUSED edge loses, and defaults to
        /// <see cref="CoverageTrimPolicy.Default"/> so that a master produced by the stacker and one
        /// cropped by <c>tianwen image autocrop</c> come out the same. A caller showing pixels to a
        /// person passes <see cref="CoverageTrimPolicy.KeepEveryPixel"/> instead.
        /// </remarks>
        public static PixelRect Trim(Image image, PixelRect start, CoverageEdgeWalkOptions? options = null, CoverageTrimPolicy? policy = null)
            => Measure(image, start, options).Apply(start, policy ?? CoverageTrimPolicy.Default);

        /// <summary>Per-edge verdicts, so a caller can report what happened as well as apply it.</summary>
        public static CoverageEdgeTrims Measure(Image image, PixelRect start, CoverageEdgeWalkOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var o = options ?? CoverageEdgeWalkOptions.Default;
            var rect = PixelRect.Intersect(start, new PixelRect(0, 0, image.Width, image.Height));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return new CoverageEdgeTrims(NotMeasurable, NotMeasurable, NotMeasurable, NotMeasurable);
            }

            return new CoverageEdgeTrims(
                MeasureEdge(image, rect, CoverageEdge.Left, o),
                MeasureEdge(image, rect, CoverageEdge.Top, o),
                MeasureEdge(image, rect, CoverageEdge.Right, o),
                MeasureEdge(image, rect, CoverageEdge.Bottom, o));
        }

        private static CoverageEdgeTrim NotMeasurable
            => new CoverageEdgeTrim(0, CoverageEdgeOutcome.NotMeasurable, EdgeRatio: double.NaN, SettleDepth: -1);

        /// <summary>Outside-in: the shallowest index from which everything inward to <paramref name="count"/>
        /// is settled, or -1 when nothing is. Read this way round because the profile is not monotone --
        /// at very low coverage a drizzle cell is fed by few drops, so its neighbours correlate and the
        /// sigma dips, which puts a false floor in the middle of the ramp for a first-crossing rule to
        /// stop at.</summary>
        private static int SettleIndex(double[] profile, int count, double settled, double margin)
        {
            var first = -1;
            for (var i = count - 1; i >= 0; i--)
            {
                if (double.IsFinite(profile[i]) && profile[i] > margin * settled)
                {
                    break;
                }

                first = i;
            }

            return first;
        }

        /// <summary>One edge, internal so the tests can pin each outcome separately.</summary>
        internal static CoverageEdgeTrim MeasureEdge(Image image, PixelRect rect, CoverageEdge edge, CoverageEdgeWalkOptions o)
        {
            var horizontal = edge is CoverageEdge.Top or CoverageEdge.Bottom;
            var span = horizontal ? rect.Height : rect.Width;
            var along = horizontal ? rect.Width : rect.Height;

            // How far the walk LOOKS, and the most it may take. The search is at least the cap, because
            // looking less far than you are willing to trim can only manufacture refusals.
            var search = (int)(Math.Max(o.SettleSearchFraction, o.MaxTrimFraction) * span) / o.Step * o.Step;
            var maxTrim = (int)(o.MaxTrimFraction * span) / o.Step * o.Step;

            // And how far the CONFIRMING look goes. The profile is measured once, out to here; the
            // settle rule then runs at both widths over the one array, which is what makes the second
            // opinion an extra scan rather than an extra pass over the pixels.
            var confirm = Math.Max((int)(Math.Max(o.ConfirmFactor, 1.0) * search) / o.Step * o.Step, search);

            // The reference pool runs from half its depth to its depth, so it has to BEGIN outside the
            // search window or it would be measured inside the band being profiled. Derive it from the
            // search rather than fixing it: with a constant ReferenceFraction, widening the window past
            // half of it made this guard fire, and the walk returned "nothing to trim" for every edge of
            // every master from about 0.075 up. A knob whose useful range ends without saying so is
            // worse than one that is merely too small.
            var reference = Math.Max((int)(o.ReferenceFraction * span), 2 * confirm + o.BandThickness);

            // Too small to say anything: a band needs three tiles before it has a percentile at all, and
            // the reference plus its band has to fit inside the span.
            if (along < 3 * o.TileLength || maxTrim < 2 * o.Step || search < 2 * o.Step
                || reference + o.BandThickness >= span)
            {
                return NotMeasurable;
            }

            var count = search / o.Step + 1;
            var countConfirm = confirm / o.Step + 1;
            var profile = new double[countConfirm];
            for (var i = 0; i < countConfirm; i++)
            {
                profile[i] = BandNoise(image, rect, edge, i * o.Step, o);
            }
            SmoothInPlace(profile);

            var settled = SettledLevel(image, rect, edge, reference, o);
            if (!double.IsFinite(settled) || settled <= 0 || !double.IsFinite(profile[0]))
            {
                return NotMeasurable;
            }

            var edgeRatio = profile[0] / settled;
            if (edgeRatio < o.MinimumRise)
            {
                // The outermost band is already as quiet as just inside. Nothing to remove -- and saying
                // so is not the same as declining, because the walk did answer.
                return new CoverageEdgeTrim(0, CoverageEdgeOutcome.Clean, edgeRatio, SettleDepth: 0);
            }

            // The same rule at both widths, over the one profile and against the one reference level, so
            // the only thing that differs between the two answers is how far the look went.
            var first = SettleIndex(profile, count, settled, o.SettleMargin);
            var firstConfirm = SettleIndex(profile, countConfirm, settled, o.SettleMargin);

            // A border answers the same index at both widths, and it does so for a structural reason
            // rather than within a tolerance: the wider scan walks in from deeper, so if everything
            // between the two limits is quiet it arrives at the narrow limit and breaks exactly where
            // the narrow scan did. They can only differ when that deeper region is NOT quiet, and then
            // what the narrow window called the settled level was not settled. No tolerance to pick.
            if (first != firstConfirm)
            {
                // Looking further changed the answer, so the answer was the window. The depth is kept
                // for diagnostics; trimming to it is exactly the mistake this outcome exists to name.
                var found = Math.Max(firstConfirm, first);
                return new CoverageEdgeTrim(0, CoverageEdgeOutcome.Unconfirmed, edgeRatio,
                    found < 0 ? -1 : found * o.Step);
            }

            if (first < 0)
            {
                // Nothing settled anywhere in either window: the noise is still coming down that far in.
                // That is the frame's own structure, not a border, so refuse rather than eat the cap.
                return new CoverageEdgeTrim(0, CoverageEdgeOutcome.NeverSettles, edgeRatio, SettleDepth: -1);
            }

            var settleDepth = first * o.Step;

            // Settled, confirmed, and past what the caller will pay. Report WHERE, so the answer is
            // actionable: it used to be indistinguishable from "still falling", which is the difference
            // between an edge you could remove for a known price and one nobody can measure at all. It
            // is also now distinct from a gradient, so a consumer may trim to this depth.
            return settleDepth > maxTrim
                ? new CoverageEdgeTrim(0, CoverageEdgeOutcome.BeyondCap, edgeRatio, settleDepth)
                : new CoverageEdgeTrim(settleDepth, settleDepth == 0 ? CoverageEdgeOutcome.Clean : CoverageEdgeOutcome.Trimmed, edgeRatio, settleDepth);
        }

        /// <summary>
        /// The level the edge's noise settles to: the median of samples between half
        /// <see cref="CoverageEdgeWalkOptions.ReferenceFraction"/> and all of it, sparsely sampled
        /// because it is a level and not a profile.
        /// </summary>
        private static double SettledLevel(Image image, PixelRect rect, CoverageEdge edge, int reference, CoverageEdgeWalkOptions o)
        {
            var stride = Math.Max(o.Step, 32);
            var from = reference / 2;
            var samples = new double[Math.Max(1, (reference - from + stride - 1) / stride)];
            var count = 0;
            for (var depth = from; depth < reference && count < samples.Length; depth += stride)
            {
                var value = BandNoise(image, rect, edge, depth, o);
                if (double.IsFinite(value))
                {
                    samples[count++] = value;
                }
            }
            if (count == 0)
            {
                return double.NaN;
            }
            Array.Sort(samples, 0, count);
            return samples[count / 2];
        }

        /// <summary>Median-of-three, in place: one loud band should not decide where the border ends.</summary>
        private static void SmoothInPlace(double[] profile)
        {
            if (profile.Length < 3)
            {
                return;
            }
            var previous = profile[0];
            for (var i = 1; i < profile.Length - 1; i++)
            {
                var a = previous;
                var b = profile[i];
                var c = profile[i + 1];
                previous = b;
                profile[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
            }
        }

        /// <summary>
        /// The noisiest channel's percentile of per-tile sigmas for the band at <paramref name="depth"/>.
        /// The worst channel decides: a band under-exposed in one channel of three is under-exposed.
        /// </summary>
        internal static double BandNoise(Image image, PixelRect rect, CoverageEdge edge, int depth, CoverageEdgeWalkOptions o)
        {
            var horizontal = edge is CoverageEdge.Top or CoverageEdge.Bottom;
            var band = BandRect(rect, edge, depth, o.BandThickness);
            if (band.Width <= 0 || band.Height <= 0
                || band.X < 0 || band.Y < 0 || band.Right > image.Width || band.Bottom > image.Height)
            {
                return double.NaN;
            }

            var worst = double.NaN;
            for (var c = 0; c < image.ChannelCount; c++)
            {
                var value = ChannelBandNoise(image.GetChannelSpan(c), image.Width, band, horizontal, o);
                if (double.IsFinite(value) && (!double.IsFinite(worst) || value > worst))
                {
                    worst = value;
                }
            }
            return worst;
        }

        private static PixelRect BandRect(PixelRect rect, CoverageEdge edge, int depth, int thickness) => edge switch
        {
            CoverageEdge.Top => new PixelRect(rect.X, rect.Y + depth, rect.Width, thickness),
            CoverageEdge.Bottom => new PixelRect(rect.X, rect.Bottom - depth - thickness, rect.Width, thickness),
            CoverageEdge.Left => new PixelRect(rect.X + depth, rect.Y, thickness, rect.Height),
            _ => new PixelRect(rect.Right - depth - thickness, rect.Y, thickness, rect.Height),
        };

        /// <summary>
        /// Per-tile sigma from the MAD of adjacent differences ALONG the band, then the requested
        /// percentile over the tiles. Differencing along the band kills any gradient across it, and
        /// across is the direction a coverage ramp runs, so the statistic answers about noise and never
        /// about level.
        /// </summary>
        private static double ChannelBandNoise(
            ReadOnlySpan<float> plane, int imageWidth, PixelRect band, bool horizontal, CoverageEdgeWalkOptions o)
        {
            var along = horizontal ? band.Width : band.Height;
            var across = horizontal ? band.Height : band.Width;
            var tiles = along / o.TileLength;
            if (tiles < 3 || across < 1)
            {
                return double.NaN;
            }

            using var sigmas = ArrayPoolHelper.Rent<float>(tiles);
            using var diffs = ArrayPoolHelper.Rent<float>(across * (o.TileLength - 1));
            var kept = 0;
            for (var t = 0; t < tiles; t++)
            {
                var start = t * o.TileLength;
                var n = 0;
                for (var a = 0; a < across; a++)
                {
                    for (var i = 0; i < o.TileLength - 1; i++)
                    {
                        int from;
                        int to;
                        if (horizontal)
                        {
                            from = (band.Y + a) * imageWidth + band.X + start + i;
                            to = from + 1;
                        }
                        else
                        {
                            from = (band.Y + start + i) * imageWidth + band.X + a;
                            to = from + imageWidth;
                        }
                        var d = plane[to] - plane[from];
                        if (float.IsFinite(d))
                        {
                            diffs[n++] = d;
                        }
                    }
                }
                if (n < 32)
                {
                    continue;
                }
                var (_, mad) = StatisticsHelper.MedianAndMad(diffs.AsSpan(0, n));
                // The difference of two independent samples carries sqrt(2) their sigma, and 1.4826
                // takes a MAD to a Gaussian sigma.
                sigmas[kept++] = (float)(1.4826 * mad / Math.Sqrt(2.0));
            }

            return kept < 3
                ? double.NaN
                : StatisticsHelper.PercentileFast(sigmas.AsSpan(0, kept), o.Percentile / 100.0);
        }
    }
}
