using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The catalog query answers brightest-first, and everything downstream assumes it.
    /// </summary>
    /// <remarks>
    /// <para>This is deliberately a test of the QUERY and not of a solve, because nothing else can
    /// be. Both consumers truncate the list as though it were a brightness ranking --
    /// <c>PairRansacLock</c> keeps the first 160 as anchors and probes the first 8 as its Stage 1
    /// gate, and the matching loop takes the first 50 then 100 on a dense field and penalises by
    /// rank -- so an unsorted list does not fail loudly, it quietly hands both of them an arbitrary
    /// spatial slice of the sky. On LDN 1089 that slice put ZERO of the first 20 anchors on real
    /// stars, the true hypothesis was discarded at Stage 1 on every one of a million tries, and the
    /// only symptom was "no pair-lock seed".</para>
    /// <para>The frozen Vela regression cannot cover this: it replaces the query with a recorded
    /// catalogue, so the replay never executes the code under test here. It stayed green through
    /// the whole bug for exactly that reason -- see the note in <c>VelaMosaicStarListExport</c>.
    /// </para>
    /// </remarks>
    public class CatalogQueryOrderTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task TheCatalogQueryReturnsBrightestFirst()
        {
            var ct = TestContext.Current.CancellationToken;
            var solver = new CatalogPlateSolver(await SharedCatalogDB.InitAsync(ct), NullLogger.Instance);

            // A dense low-galactic-latitude field, so the query walks many grid cells and any
            // scan-order leak is obvious rather than marginal. (Cygnus, near LDN 1089.)
            var stars = solver.QueryCatalogStarsInRegion(new WCS(20.552, 63.476), 2.0, 0.0);
            stars.Count.ShouldBeGreaterThan(200, "the probe field must be dense enough to order meaningfully");

            var outOfOrder = 0;
            for (var i = 1; i < stars.Count; i++)
            {
                if (stars[i].VMag < stars[i - 1].VMag)
                {
                    outOfOrder++;
                }
            }

            output.WriteLine($"{stars.Count} stars, V {stars[0].VMag:F2}..{stars[^1].VMag:F2}, " +
                $"{outOfOrder} inversions");
            outOfOrder.ShouldBe(0, "every consumer truncates this list as a brightness ranking");

            // And the head must actually be the bright end -- an ordering assertion alone passes on
            // a list sorted the wrong way, and faintest-first would be a far worse anchor pool than
            // the scan order this replaced.
            stars[0].VMag.ShouldBeLessThan(stars[^1].VMag);
        }
    }
}
