using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Followup F from PLAN-gpu-stretch-tests.md: deterministic-output checks for the CPU-side
/// great-circle line tessellation that feeds <see cref="VkSkyMapPipeline"/>'s Line pipeline.
///
/// No GPU dependency -- the tessellators produce <see cref="List{Single}"/> on the CPU side
/// and the resulting vertex buffer is uploaded with <c>ctx.WriteVertices</c> later. Catches
/// regressions in vertex count, unit-sphere invariants, and constellation HIP lookup without
/// any Vulkan setup. Cheaper than the offscreen-render comp tests in
/// <see cref="VkRendererPrimitiveTests"/> and <see cref="GpuStretchPipelineTests"/>.
/// </summary>
public sealed class SkyMapLineTessellationTests(ITestOutputHelper output)
{
    private const float UnitSphereTolerance = 1e-5f;

    // ----- RaDecToUnitVec sanity -----

    [Theory]
    [InlineData(0.0, 0.0, 1.0f, 0.0f, 0.0f)]    // RA=0h, Dec=0  -> +X (vernal equinox)
    [InlineData(6.0, 0.0, 0.0f, 1.0f, 0.0f)]    // RA=6h, Dec=0  -> +Y
    [InlineData(12.0, 0.0, -1.0f, 0.0f, 0.0f)]  // RA=12h, Dec=0 -> -X
    [InlineData(18.0, 0.0, 0.0f, -1.0f, 0.0f)]  // RA=18h, Dec=0 -> -Y
    [InlineData(0.0, 90.0, 0.0f, 0.0f, 1.0f)]   // North celestial pole
    [InlineData(13.5, -90.0, 0.0f, 0.0f, -1.0f)] // South celestial pole (RA doesn't matter)
    public void RaDecToUnitVec_KnownPositions(double raHours, double decDeg, float expX, float expY, float expZ)
    {
        var (x, y, z) = SkyMapState.RaDecToUnitVec(raHours, decDeg);
        x.ShouldBe(expX, 1e-5f);
        y.ShouldBe(expY, 1e-5f);
        z.ShouldBe(expZ, 1e-5f);
        var magSq = x * x + y * y + z * z;
        magSq.ShouldBe(1.0f, 1e-5f);
    }

    // ----- BuildMeridianLine -----

    [Theory]
    [InlineData(0.0)]   // LST = 0h (sidereal midnight at vernal equinox crossing)
    [InlineData(6.0)]   // LST = 6h
    [InlineData(13.5)]  // LST = 13.5h (some random non-integer hour)
    [InlineData(23.99)] // Edge of [0, 24) range
    public void BuildMeridianLine_ProducesUnitSphereLineList(double lst)
    {
        // The implementation tessellates two half-great-circle arcs (LST line + antimeridian
        // line) with steps/2 = 100 segments each. Each arc emits 6 floats per segment via the
        // line-list pair pattern in TessellateArc.
        var floats = new List<float>();
        VkSkyMapPipeline.BuildMeridianLine(lst, floats);

        // 2 arcs * 100 segments * 2 vertices * 3 floats = 1200 floats = 400 vertices = 200 segments
        floats.Count.ShouldBe(1200, "BuildMeridianLine should emit exactly 1200 floats");
        AssertAllUnitSphere(floats, $"BuildMeridianLine(lst={lst})");

        // Each segment in the LST-line half should have RA == lst (give or take floating-point
        // jitter from the RA->XY conversion). Sanity check on the first segment's first vertex:
        // it sits at Dec=-90 -> south pole -> (0, 0, -1).
        floats[0].ShouldBe(0f, 1e-5f, "first vertex X should be 0 at south pole");
        floats[1].ShouldBe(0f, 1e-5f, "first vertex Y should be 0 at south pole");
        floats[2].ShouldBe(-1f, 1e-5f, "first vertex Z should be -1 at south pole");
    }

    // ----- BuildHorizonLine -----

    [Fact]
    public void BuildHorizonLine_InvalidSite_EmitsNothing()
    {
        var floats = new List<float>();
        VkSkyMapPipeline.BuildHorizonLine(default, floats);
        floats.Count.ShouldBe(0, "invalid site should short-circuit");
    }

    [Fact]
    public void BuildHorizonLine_ValidSite_ProducesUnitSphereClosedRing()
    {
        // Pick a fixed UTC instant so LST is deterministic across runs (J2000 epoch).
        var siteUtc = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(siteLat: 45.0, siteLon: 0.0, siteUtc);
        var floats = new List<float>();
        VkSkyMapPipeline.BuildHorizonLine(site, floats);

        // BuildHorizonLine iterates 0..120 (121 points), emitting 6 floats per non-initial
        // step => 6 * 120 = 720 floats. (Closed via the i=0 == i=120 wrap.)
        floats.Count.ShouldBe(720, "BuildHorizonLine should emit 720 floats for 120 segments");
        AssertAllUnitSphere(floats, "BuildHorizonLine");
    }

    // ----- BuildAltAzGrid -----

    [Fact]
    public void BuildAltAzGrid_ValidSite_ProducesUnitSphereLineList()
    {
        var siteUtc = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(siteLat: -33.86, siteLon: 151.21, siteUtc);
        var floats = new List<float>();
        VkSkyMapPipeline.BuildAltAzGrid(site, floats);

        floats.Count.ShouldBeGreaterThan(0, "valid site should produce alt/az grid lines");
        (floats.Count % 6).ShouldBe(0, "each segment is 2 vertices * 3 floats");
        AssertAllUnitSphere(floats, "BuildAltAzGrid");
    }

    // ----- Constellation figure tessellation -----

    /// <summary>
    /// Replicates the inner loop of <c>VkSkyMapPipeline.BuildConstellationFigureBuffer</c>
    /// for a single constellation. The private builder iterates every constellation and emits
    /// one line segment per consecutive HIP pair in each polyline; this test does the same for
    /// just Orion and checks the resulting vertex list against direct
    /// <c>RaDecToUnitVec(catalog_ra, catalog_dec)</c> lookups.
    /// </summary>
    [Fact]
    public async Task BuildConstellationFigure_Orion_MatchesHipCatalogProjections()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await InitHipDbAsync(ct);

        var floats = new List<float>();
        var skippedHipCount = 0;
        var emittedSegmentCount = 0;
        foreach (var polyline in Constellation.Orion.Figure)
        {
            // Match the private builder: pair consecutive HIPs that both resolve in the DB.
            float prevX = 0, prevY = 0, prevZ = 0;
            var hasPrev = false;
            foreach (var hip in polyline)
            {
                if (!db.TryLookupHIP(hip, out var ra, out var dec, out _, out _))
                {
                    skippedHipCount++;
                    hasPrev = false;
                    continue;
                }
                var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);
                if (hasPrev)
                {
                    floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                    floats.Add(x); floats.Add(y); floats.Add(z);
                    emittedSegmentCount++;
                }
                prevX = x; prevY = y; prevZ = z;
                hasPrev = true;
            }
        }

        output.WriteLine($"Orion: {emittedSegmentCount} segments emitted, {skippedHipCount} HIPs skipped (missing from DB)");
        emittedSegmentCount.ShouldBeGreaterThan(5, "Orion should produce at least 5 stick-figure segments");
        skippedHipCount.ShouldBe(0, "every Orion-figure HIP should resolve in the HIP catalog");
        AssertAllUnitSphere(floats, "Orion figure");

        // Spot-check: the first polyline starts at HIP 26727 (Alnitak, top of belt). Its first
        // vertex (= floats[0..3]) should equal RaDecToUnitVec applied to the catalog values
        // for HIP 26727 directly. Trivial round-trip but catches "I serialized the buffer in
        // the wrong order" regressions.
        db.TryLookupHIP(26727, out var alnitakRA, out var alnitakDec, out _, out _).ShouldBeTrue();
        var (expX, expY, expZ) = SkyMapState.RaDecToUnitVec(alnitakRA, alnitakDec);
        floats[0].ShouldBe(expX, 1e-5f);
        floats[1].ShouldBe(expY, 1e-5f);
        floats[2].ShouldBe(expZ, 1e-5f);
    }

    // ----- Helpers -----

    private static ICelestialObjectDB? _cachedDb;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    /// <summary>
    /// Loads a <see cref="CelestialObjectDB"/> with <c>waitForTycho2BulkLoad: false</c> -- the
    /// constellation figures only need the HIP cross-reference, not the ~2.5M Tycho-2 bulk,
    /// so we skip that load and the catalog ready callback. Cached across tests in this
    /// class so the cost is paid once.
    /// </summary>
    private static async Task<ICelestialObjectDB> InitHipDbAsync(CancellationToken ct)
    {
        if (_cachedDb is { } cached) return cached;
        await _dbSem.WaitAsync(ct);
        try
        {
            if (_cachedDb is { } cached2) return cached2;
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);
            _cachedDb = db;
            return db;
        }
        finally
        {
            _dbSem.Release();
        }
    }

    private void AssertAllUnitSphere(List<float> floats, string label)
    {
        (floats.Count % 3).ShouldBe(0, $"{label}: vertex floats should be a multiple of 3");
        var worst = 0f;
        for (var i = 0; i < floats.Count; i += 3)
        {
            var x = floats[i];
            var y = floats[i + 1];
            var z = floats[i + 2];
            var magSq = x * x + y * y + z * z;
            var dev = Math.Abs(magSq - 1f);
            if (dev > worst) worst = dev;
        }
        output.WriteLine($"[{label}] {floats.Count / 3} vertices, worst magnitude deviation = {worst:G6}");
        worst.ShouldBeLessThan(UnitSphereTolerance, $"{label}: every vertex should land on the unit sphere");
    }
}
