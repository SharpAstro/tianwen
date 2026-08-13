using Shouldly;
using System.Collections.Generic;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The census exists to separate the two opposite causes of a registration wipe-out, so the tests
/// are one per cause plus the trend, and they use the numbers from the session that was misdiagnosed.
/// </summary>
public class RegistrationCensusTests
{
    /// <summary>
    /// Modelled on "Segaull+Thors_Helmet" / HIP 42861, whose 49 subs were dropped and written up as
    /// "genuinely too star-poor to register". Reconstructed from the Debug log, its real spread was
    /// stars 44/70/97 and quads 26/46/72 against a reference holding 58 quads, which is the PURITY
    /// case: both sides have plenty, none correspond. The census has to make that unmistakable.
    /// </summary>
    [Fact]
    public void TheMisdiagnosedSessionReadsAsHealthyCountsNotAStarPoorField()
    {
        // Declining through the night, which is what the real session did (r = -0.38).
        var stars = new List<int>();
        var quads = new List<int>();
        var hfd = new List<float>();
        var ecc = new List<float>();
        for (var i = 0; i < 48; i++)
        {
            var s = 97 - i;                       // 97 down to 50
            stars.Add(i == 47 ? 44 : s);
            quads.Add(72 - (i * 46 / 47));        // 72 down to 26
            hfd.Add(1.88f + (i * 0.05f));         // focus opening up
            ecc.Add(0.48f);
        }

        var line = RegistrationCensus.Describe(stars, quads, hfd, ecc);

        line.ShouldContain("48 subs");
        // The whole point: every frame is far above the matching floor, so "star-poor" is refuted
        // by the line itself rather than by re-parsing a Debug log weeks later.
        line.ShouldContain("stars 44/");
        line.ShouldContain("/97");
        line.ShouldNotContain("stars 0-24");
        // Quads healthy on the subs too, so a reader comparing against the reference's 58 sees the
        // purity case rather than a detection shortfall.
        line.ShouldContain("quads 26/");
        line.ShouldContain("hfd 1.88/");
        line.ShouldContain("DEGRADING through the session");
    }

    /// <summary>The opposite cause: a genuinely sparse field, where the counts sit in the low buckets.</summary>
    [Fact]
    public void AGenuinelySparseFieldPutsEveryFrameInTheLowBuckets()
    {
        var stars = new List<int> { 9, 11, 8, 12, 10, 9 };
        var quads = new List<int> { 1, 2, 0, 2, 1, 1 };
        var hfd = new List<float> { 2.1f, 2.0f, 2.2f, 2.1f, 2.0f, 2.1f };
        var ecc = new List<float> { 0.3f, 0.3f, 0.31f, 0.3f, 0.29f, 0.3f };

        var line = RegistrationCensus.Describe(stars, quads, hfd, ecc);

        line.ShouldContain("stars 8/");
        line.ShouldContain("0-24:6");
        // Flat, so no trend claim: a sparse field is not a degrading one and must not read as one.
        line.ShouldNotContain("DEGRADING");
    }

    /// <summary>
    /// A perfectly steady session has an UNDEFINED correlation, not a zero one. Reporting r=+0.00
    /// there would be a divide-by-zero dressed up as a measurement.
    /// </summary>
    [Fact]
    public void AConstantStarCountReportsNoTrendAtAllRatherThanZero()
    {
        var stars = new List<int> { 500, 500, 500, 500, 500 };
        var quads = new List<int> { 90, 90, 90, 90, 90 };
        var hfd = new List<float> { 3f, 3f, 3f, 3f, 3f };
        var ecc = new List<float> { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };

        var line = RegistrationCensus.Describe(stars, quads, hfd, ecc);

        line.ShouldContain("stars 500/500/500");
        line.ShouldNotContain("capture order");
    }

    [Fact]
    public void NoSurvivorsSaysSoInsteadOfRenderingAnEmptySpread()
    {
        RegistrationCensus.Describe([], [], [], []).ShouldBe("no survivors to census");
    }

    /// <summary>
    /// Frames dropped at the star floor never reach quad-forming, so the quad list is shorter than
    /// the star list and the renderer must not assume they are parallel.
    /// </summary>
    [Fact]
    public void AShorterQuadListThanStarListIsRenderedWithoutMisalignment()
    {
        var stars = new List<int> { 12, 400, 420, 410 };
        var quads = new List<int> { 80, 84, 82 };   // the 12-star frame never formed quads
        var hfd = new List<float> { 5f, 3f, 3.1f, 3f };
        var ecc = new List<float> { 0.6f, 0.2f, 0.21f, 0.2f };

        var line = RegistrationCensus.Describe(stars, quads, hfd, ecc);

        line.ShouldContain("4 subs");
        line.ShouldContain("stars 12/");
        line.ShouldContain("quads 80/82/84");
    }
}
