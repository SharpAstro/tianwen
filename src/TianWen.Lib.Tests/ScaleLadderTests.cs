using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="ScaleLadder"/> against the frozen Vela mosaic: 24 real pointings, each told only where its header
/// points and never its scale (#2249). As for the recovery it is built on, two properties are tested apart: where the
/// ladder answers, the answer is the frame's scale; and where it has nothing to go on, it declines.
/// </summary>
/// <remarks>
/// The frozen catalogue covers the mosaic and no more, so the ladder's widest rungs see a catalogue cut off at the
/// mosaic's edge where the sky goes on. That can only cost answers, never make one right: a quad matches real stars or
/// none. What the ladder does on whole frames against the whole catalogue is <c>ScaleLadderProbe</c>'s and
/// <c>RealFrameSolveTests</c>' to say.
/// </remarks>
[Collection("Astrometry")]
public class ScaleLadderTests(ITestOutputHelper output)
{
    /// <summary>
    /// The recovery's own bar (<c>QuadScaleRecoveryTests</c>): its worst case over these panels is 0.065 %, and a prior
    /// this near is inside the seed's narrow window with room to spare.
    /// </summary>
    private const double AccuracyBarPercent = 0.15;

    /// <summary>Keeps what the ladder logged, so a decline can say which rungs were asked and what they answered.</summary>
    private sealed class LinesLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public void TheLadderFindsAPanelsScaleToldOnlyWhereItPoints()
    {
        var manifest = VelaMosaicStarLists.Manifest;
        var catalogue = manifest.CatalogTuples();
        var answered = 0;
        var declined = new List<string>();
        var worstPercent = 0.0;
        var mostRungs = 0;

        foreach (var panel in manifest.Panels)
        {
            var frame = panel.Frames[0];
            var log = new LinesLogger();
            if (ScaleLadder.TryFind(frame.DetectedPoints(), catalogue, frame.Hint, panel.Width, panel.Height, log) is not { } found)
            {
                declined.Add(panel.Id);
                var hintOffDeg = CoordinateUtils.AngularSeparationDeg(frame.HintRA, frame.HintDec, frame.Wcs.CenterRA, frame.Wcs.CenterDec);
                output.WriteLine($"{panel.Id} declined ({frame.Detected.Length} detected, hint {hintOffDeg / panel.FovDeg:F2} of a frame off the solved centre):");
                foreach (var line in log.Lines)
                {
                    output.WriteLine($"    {line}");
                }

                continue;
            }

            answered++;
            mostRungs = Math.Max(mostRungs, found.RungsAsked);
            var truth = frame.Wcs.PixelScaleArcsec;
            var errorPercent = 100.0 * Math.Abs(found.ScaleArcsec - truth) / truth;
            worstPercent = Math.Max(worstPercent, errorPercent);
            errorPercent.ShouldBeLessThan(AccuracyBarPercent,
                $"{panel.Id}: the ladder answered {found.ScaleArcsec:F4} arcsec/px against the solved {truth:F4} "
                + $"(rung {found.RungsAsked} at {found.RungScaleArcsec:F3}, {found.Candidates} candidates, confirmed by {found.ConfirmCandidates})");
        }

        output.WriteLine($"answered on {answered}/{manifest.Panels.Length} panels, worst {worstPercent:F3} % off, at most {mostRungs} rungs asked; "
            + $"declined: {(declined.Count == 0 ? "none" : string.Join(", ", declined))}");

        // An always-declining ladder passes the accuracy bar with nothing to check, so the count is asserted too. All 24
        // answer as measured; two may go before this fails, as the recovery's own test allows itself.
        answered.ShouldBeGreaterThanOrEqualTo(manifest.Panels.Length - 2, "the ladder must answer on real fields, not merely avoid being wrong");
    }

    /// <summary>
    /// Pointed at another panel's sky, the ladder has no stars in common with the frame. Whatever it then says must not
    /// be a wrong scale: declining is right, and so is the true scale where the two fields do overlap after all.
    /// </summary>
    [Fact]
    public void ALadderPointedAtAnotherFieldNeverInventsAScale()
    {
        var manifest = VelaMosaicStarLists.Manifest;
        var catalogue = manifest.CatalogTuples();
        var pointedAway = 0;
        var declined = 0;

        foreach (var panel in manifest.Panels)
        {
            var frame = panel.Frames[0];

            // The panel whose header points farthest from this one's.
            var elsewhere = frame.Hint;
            var farthest = 0.0;
            foreach (var other in manifest.Panels)
            {
                var hint = other.Frames[0].Hint;
                var separation = CoordinateUtils.AngularSeparationDeg(frame.HintRA, frame.HintDec, hint.CenterRA, hint.CenterDec);
                if (separation > farthest)
                {
                    (farthest, elsewhere) = (separation, hint);
                }
            }

            // Two square frames a diagonal apart share no sky at any camera angle. A panel in the middle of the mosaic
            // has no other that far from it, and is left out.
            if (farthest <= Math.Sqrt(2.0) * panel.FovDeg)
            {
                continue;
            }

            pointedAway++;
            if (ScaleLadder.TryFind(frame.DetectedPoints(), catalogue, elsewhere, panel.Width, panel.Height) is not { } found)
            {
                declined++;
                continue;
            }

            var truth = frame.Wcs.PixelScaleArcsec;
            (100.0 * Math.Abs(found.ScaleArcsec - truth) / truth).ShouldBeLessThan(AccuracyBarPercent,
                $"{panel.Id} pointed {farthest:F1} deg away: the ladder answered {found.ScaleArcsec:F4} arcsec/px, which is not the frame's {truth:F4}");
        }

        output.WriteLine($"of {pointedAway} panels pointed at a field that shares no sky with them, {declined} were declined");
        pointedAway.ShouldBeGreaterThanOrEqualTo(4, "premise: the mosaic holds panels a frame diagonal apart");
        // The count itself, since docs/plans/plate-solver-performance.md quotes it: the manifest is frozen data, so a
        // change in the manifest or in the rule above that moves it fails here instead of leaving the plan's figure stale.
        pointedAway.ShouldBe(22);

        // The figure the plan quotes, pinned so it is a tested one. The invariant is the bar above (never a wrong
        // scale): an answer here would not be wrong if it were the frame's own scale, which a wide rung's frame can
        // reach the panel's stars for. So a change that moves this count is re-read against that bar, not reverted.
        declined.ShouldBe(pointedAway, "every panel pointed at another field was declined when this was measured (2026-11-20)");
    }

    [Fact]
    public void AFieldWithTooFewStarsIsDeclinedBeforeAnyRungIsAsked()
    {
        var manifest = VelaMosaicStarLists.Manifest;
        var panel = manifest.Panels[0];
        var frame = panel.Frames[0];
        var few = manifest.CatalogTuples().GetRange(0, QuadScaleRecovery.MinStars - 1);

        // The ladder says a line for every rung it asks (ScaleLadder.TryFind, before the question), so no line is no rung asked.
        var fewCatalogue = new LinesLogger();
        ScaleLadder.TryFind(frame.DetectedPoints(), few, frame.Hint, panel.Width, panel.Height, fewCatalogue).ShouldBeNull();
        fewCatalogue.Lines.ShouldBeEmpty();

        var fewDetections = new LinesLogger();
        ScaleLadder.TryFind(frame.DetectedPoints(QuadScaleRecovery.MinStars - 1), manifest.CatalogTuples(), frame.Hint, panel.Width, panel.Height, fewDetections)
            .ShouldBeNull();
        fewDetections.Lines.ShouldBeEmpty();
    }

    /// <summary>
    /// The first rung believes in a frame <see cref="ScaleLadder.WidestFieldDeg"/> along its long side, whichever side
    /// that is, and the query reaches that frame's corners with the projection's margin.
    /// </summary>
    [Theory]
    [InlineData(3000, 2000)]
    [InlineData(2000, 3000)]
    public void TheFirstRungAndItsQueryFollowTheFramesLongSide(int width, int height)
    {
        var widest = ScaleLadder.WidestScaleArcsec(width, height);
        (widest * 3000 / 3600.0).ShouldBe(ScaleLadder.WidestFieldDeg, 1e-9);

        // 1.2: the frame with a tenth of itself kept on each side. The margin is a float, hence the tolerance.
        var halfDiagonalDeg = 0.5 * Math.Sqrt((3000.0 * 3000.0) + (2000.0 * 2000.0)) * widest / 3600.0;
        ScaleLadder.QueryRadiusDeg(width, height).ShouldBe(1.2 * halfDiagonalDeg, 1e-6);
    }
}
