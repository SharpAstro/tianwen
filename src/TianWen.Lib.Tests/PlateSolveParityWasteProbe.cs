using System;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Baseline for phase A of <c>docs/plans/plate-solver-performance.md</c>: how much of the
    /// geometric seed's work is spent on the parity that was never going to lock.
    ///
    /// <para>The solver races both parities (<c>xSign</c> +1 and -1) as <c>Task.Run</c> siblings
    /// and keeps the better one, so on every solve one of the two is pure waste. The plan quotes
    /// 32,179 hypotheses on the winner against 915,994 on the loser for a single frame; this walks
    /// the whole frozen mosaic and reports the same split over 96 real frames, per anchor-pool
    /// policy, so the phase has a number to be measured against rather than one hand-timed frame.
    /// </para>
    ///
    /// <para><b>Hypotheses, not milliseconds, on purpose.</b> The count is deterministic for a
    /// given input and identical on any machine, so it can be compared across builds and across
    /// the phase landing; wall clock here would mostly measure this box. The wall-clock budget
    /// (init / matching / detection / catalog query) is a separate harness and a separate phase.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Env-gated because it drives every frame of the mosaic through both parities -- the same work
    /// as <c>VelaMosaicFieldTests</c>, which is already the slowest class in the unit suite.
    /// Set <c>TIANWEN_PARITY_WASTE</c> to run it.
    /// </remarks>
    [Collection("Astrometry")]
    public class PlateSolveParityWasteProbe(ITestOutputHelper output)
    {
        [Fact]
        public void ReportHowMuchWorkTheLosingParityCosts()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_PARITY_WASTE")),
                "Set TIANWEN_PARITY_WASTE=1 to run the parity-waste baseline");

            var manifest = VelaMosaicStarLists.Manifest;
            var catalog = manifest.CatalogTuples();

            long wonHypotheses = 0, lostHypotheses = 0;
            int frames = 0, framesWhereBothLocked = 0, framesWhereNeitherLocked = 0;
            var worstLoser = 0;
            var worstLoserName = "";

            foreach (var panel in manifest.Panels)
            {
                var dim = panel.Dim;
                var pixelScaleRad = double.DegreesToRadians(dim.PixelScale / 3600.0);
                var cx = dim.Width / 2.0;
                var cy = dim.Height / 2.0;

                foreach (var frame in panel.Frames)
                {
                    var det = frame.DetectedPoints();
                    frames++;

                    // Exactly the pair the solver races, in the same order.
                    var attempts = new (double XSign, PairRansacLock.LockResult? Lock, CatalogPlateSolver.SeedCost Cost)[2];
                    var i = 0;
                    foreach (var xSign in new[] { -1.0, 1.0 })
                    {
                        var got = CatalogPlateSolver.TrySeedPairLock(
                            catalog, det, frame.Hint, pixelScaleRad, cx, cy, dim, xSign,
                            scaleTolerance: 0.03f, out var cost,
                            cancellationToken: TestContext.Current.CancellationToken);
                        attempts[i++] = (xSign, got, cost);
                    }

                    var lockedCount = 0;
                    foreach (var a in attempts)
                    {
                        if (a.Lock is not null)
                        {
                            lockedCount++;
                        }
                    }

                    if (lockedCount == 0)
                    {
                        framesWhereNeitherLocked++;
                    }
                    else if (lockedCount == 2)
                    {
                        framesWhereBothLocked++;
                    }

                    // The winner is the higher consensus, as the solver picks it; everything the
                    // other one spent is what phase A stands to remove.
                    var bestIdx = attempts[0].Lock is { } l0
                        && (attempts[1].Lock is not { } l1 || l0.Hits >= l1.Hits) ? 0 : 1;
                    for (var k = 0; k < attempts.Length; k++)
                    {
                        // The cost overload sums every pool policy tried, so a parity that never
                        // locked is charged what it actually spent rather than an assumed cap.
                        var spent = attempts[k].Cost.Hypotheses;
                        if (k == bestIdx && attempts[k].Lock is not null)
                        {
                            wonHypotheses += spent;
                        }
                        else
                        {
                            lostHypotheses += spent;
                            if (spent > worstLoser)
                            {
                                worstLoser = spent;
                                worstLoserName = $"{panel.Id}/{attempts[k].XSign:+0;-0}";
                            }
                        }
                    }
                }
            }

            var total = wonHypotheses + lostHypotheses;
            output.WriteLine($"frames                 {frames}");
            output.WriteLine($"both parities locked   {framesWhereBothLocked}");
            output.WriteLine($"neither locked         {framesWhereNeitherLocked}");
            output.WriteLine($"winning-parity work    {wonHypotheses,12:N0} hypotheses");
            output.WriteLine($"losing-parity work     {lostHypotheses,12:N0} hypotheses  <- what phase A removes");
            output.WriteLine($"total                  {total,12:N0}");
            output.WriteLine($"waste share            {(total > 0 ? 100.0 * lostHypotheses / total : 0):F1}%");
            output.WriteLine($"worst single loser     {worstLoser:N0} ({worstLoserName})");
        }
    }
}
