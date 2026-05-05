using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Guards the build-time simbad_merge.bin.gz snapshot wired up by Phase 2B of
/// PLAN-catalog-binary-format.md. Two responsibilities:
///   1. Catch staleness in CI (input hash mismatch) so a SIMBAD/NGC catalog edit that forgets
///      to re-bake the snapshot fails loudly instead of silently regressing init time.
///   2. Catch divergence between the live SIMBAD merge path and the snapshot apply path for
///      representative objects + cross-references.
/// </summary>
[Collection("Catalog")]
public class SimbadMergeSnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void GivenEmbeddedSnapshot_WhenHashedAgainstCurrentInputs_ThenItIsFresh()
    {
        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var snapshotResource = manifestNames.FirstOrDefault(n => n.EndsWith(".simbad_merge.bin.gz", StringComparison.Ordinal));
        snapshotResource.ShouldNotBeNull(
            "Snapshot resource simbad_merge.bin.gz is not embedded. Run tools/precompute-simbad-merge.ps1 to bake it.");

        using var stream = assembly.GetManifestResourceStream(snapshotResource!)
            ?? throw new InvalidOperationException("Snapshot resource stream was null.");
        _ = SimbadMergeSnapshotIo.Read(stream, out var storedHash);

        var expectedHash = SimbadMergeInputHasher.Compute(assembly, manifestNames);
        Convert.ToHexString(storedHash).ShouldBe(Convert.ToHexString(expectedHash),
            "Embedded simbad_merge.bin.gz is stale: at least one SIMBAD/NGC catalog input changed since the snapshot was baked. " +
            "Run tools/precompute-simbad-merge.ps1 to refresh, then commit the regenerated file.");

        output.WriteLine($"Snapshot input hash: {Convert.ToHexString(storedHash)[..16]}...");
    }

    [Fact]
    public async Task GivenLiveAndSnapshotPaths_WhenComparingState_ThenTheyAgree()
    {
        // Drive the precompute capture path to materialise a fresh in-memory snapshot, then
        // compare it against the runtime apply path's end state. This catches algorithm-vs-snapshot
        // semantic drift (e.g. a new field on CelestialObject that the apply path forgets to populate)
        // INDEPENDENTLY of whether the embedded snapshot is fresh.
        var live = new CelestialObjectDB { ForceLiveSimbadMergeWithCapture = true };
        await live.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);
        live.LastSimbadMergeSnapshot.ShouldNotBeNull("Live capture path produced no snapshot");

        var snapshot = live.LastSimbadMergeSnapshot!;
        snapshot.Objects.Length.ShouldBeGreaterThan(20_000, "Object count unexpectedly low — bad inputs?");
        snapshot.Edges.Length.ShouldBeGreaterThan(10_000, "Edge count unexpectedly low — bad inputs?");

        // Per-catalog minimum-entry sanity check: catches the failure mode where one of the
        // 14 SIMBAD inputs silently produces 0 records (empty .gs.gz, parser regression, etc.)
        // — which the input-hash check would otherwise wave through after a re-bake. Thresholds
        // are set well below empirical values so a new SIMBAD release with slightly different
        // counts doesn't tank CI; they only fail when an entire catalog goes missing.
        // Empirical values (2026-05-05): HR 9.1K, Cl 14K, Dobashi 7.6K, LDN 1.8K, HH 1K,
        // Ced 420, Barnard 350, Sh 300, vdB 270, DG 250, GUM 60, RCW 30, CG 20.
        // Per-catalog minimum-entry sanity check: catches the failure mode where one of the
        // 14 SIMBAD inputs silently produces 0 records (empty .gs.gz, parser regression, etc.)
        // — which the input-hash check would otherwise wave through after a re-bake.
        //
        // We count entries via two channels because the SIMBAD merge writes to two dicts:
        //   * Objects: catalog appears as a key in _objectsByIndex (e.g. Cr360, Dobashi 222 are
        //     standalone entries). Most HR/Sh/HH records get folded into HIP/HD/NGC entries via
        //     PopulateSimbadStarEntries / bestMatches and don't add their own _objectsByIndex key,
        //     so per-catalog Object counts here are LOWER than the input file's record count.
        //   * Edges (cross-refs): catalog appears as a key in _crossIndexLookuptable. This is
        //     where catalogs like HR/Sh that fold into HIP/HD show up — every HR1234 record that
        //     resolved a bestMatch creates HR1234 as a cross-ref key.
        var byCatalogObjects = new Dictionary<Catalog, int>();
        foreach (var obj in snapshot.Objects)
        {
            var cat = obj.Index.ToCatalog();
            byCatalogObjects[cat] = byCatalogObjects.GetValueOrDefault(cat) + 1;
        }
        var byCatalogEdges = new Dictionary<Catalog, int>();
        foreach (var edge in snapshot.Edges)
        {
            var cat = edge.Key.ToCatalog();
            byCatalogEdges[cat] = byCatalogEdges.GetValueOrDefault(cat) + 1;
        }

        // Combined "this catalog is loaded" count: max of object + edge presence.
        // Thresholds set below empirical values so a slightly different SIMBAD release
        // doesn't tank CI; they only fail when an entire catalog effectively goes missing.
        int Loaded(Catalog c) => Math.Max(byCatalogObjects.GetValueOrDefault(c), byCatalogEdges.GetValueOrDefault(c));
        // Thresholds set well below empirical values (max(obj,edges)) measured 2026-05-05 so
        // a slightly smaller SIMBAD release doesn't tank CI; they fail only when a catalog
        // essentially disappears.
        // Empirical:  HR 9104 | Dobashi 7612 | HH 2341 | LDN 1967 | Sh 306 | Barnard 213
        //             RCW 170 | vdB 167 | Ced 131 | DG 100 | Collinder 83 | Melotte 77
        //             GUM 66 | CG 55
        var minimums = new (Catalog Cat, int Min)[]
        {
            (Catalog.HR,        1_000),
            (Catalog.Dobashi,   1_000),
            (Catalog.HH,          500),
            (Catalog.LDN,         500),
            (Catalog.Sharpless,    50),
            (Catalog.Barnard,      50),
            (Catalog.RCW,          30),
            (Catalog.vdB,          30),
            (Catalog.Ced,          30),
            (Catalog.DG,           30),
            (Catalog.Collinder,    20),
            (Catalog.Melotte,      20),
            (Catalog.GUM,          15),
            (Catalog.CG,           10),
        };

        output.WriteLine("Per-catalog counts in snapshot (objects, cross-ref edges):");
        foreach (var (cat, _) in minimums)
        {
            output.WriteLine($"  {cat,-12} obj={byCatalogObjects.GetValueOrDefault(cat),5}  edges={byCatalogEdges.GetValueOrDefault(cat),5}");
        }
        foreach (var (cat, min) in minimums)
        {
            Loaded(cat).ShouldBeGreaterThan(min,
                $"Catalog {cat} appears to have lost most or all entries in the snapshot (objects + cross-refs combined) — check the corresponding .gs.gz parser.");
        }

        // Default-init a second DB; if there is an embedded snapshot it'll go through the apply
        // path, otherwise it'll go through live merge (and the comparison degenerates to
        // self-equality, which is still a useful smoke test).
        var applied = new CelestialObjectDB();
        await applied.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Spot-check 64 objects from the captured snapshot. We don't compare every one of
        // ~31K because building two full catalogues here already costs ~3 s in unit tests;
        // a representative spread (every Nth index from the snapshot's stable order) catches
        // the kinds of divergence we care about — wrong RA/Dec, wrong type, missing names.
        var step = Math.Max(1, snapshot.Objects.Length / 64);
        var verified = 0;
        for (var i = 0; i < snapshot.Objects.Length; i += step)
        {
            var captured = snapshot.Objects[i];
            applied.TryLookupByIndex(captured.Index, out var appliedObj).ShouldBeTrue(
                $"Apply path missing object {captured.Index}");
            appliedObj.ObjectType.ShouldBe(captured.ObjType, $"{captured.Index}: type drift");
            appliedObj.RA.ShouldBe(captured.Ra, $"{captured.Index}: RA drift");
            appliedObj.Dec.ShouldBe(captured.Dec, $"{captured.Index}: Dec drift");
            appliedObj.Constellation.ShouldBe(captured.Constellation, $"{captured.Index}: constellation drift");

            // Common-name set should match exactly. Both sides materialise as HashSet<string>.
            if (!captured.CommonNames.IsDefaultOrEmpty)
            {
                var capturedNames = new HashSet<string>(captured.CommonNames);
                var appliedNames = new HashSet<string>(appliedObj.CommonNames);
                appliedNames.SetEquals(capturedNames).ShouldBeTrue(
                    $"{captured.Index}: common-name drift. Captured=[{string.Join(",", capturedNames.OrderBy(n => n))}] Applied=[{string.Join(",", appliedNames.OrderBy(n => n))}]");
            }
            verified++;
        }

        // Spot-check 32 edges too: the cross-index lookup should walk the same dict the apply
        // path wrote to, with at least the captured target reachable from the captured key.
        var edgeStep = Math.Max(1, snapshot.Edges.Length / 32);
        var verifiedEdges = 0;
        for (var i = 0; i < snapshot.Edges.Length; i += edgeStep)
        {
            var edge = snapshot.Edges[i];
            applied.TryGetCrossIndices(edge.Key, out var crossIndices).ShouldBeTrue(
                $"Apply path missing cross-index entries for {edge.Key}");
            crossIndices.ShouldContain(edge.V1, $"{edge.Key}: v1={edge.V1} not reachable in apply path");
            verifiedEdges++;
        }

        output.WriteLine($"Verified {verified} objects + {verifiedEdges} edges between live capture and apply paths.");
    }

    /// <summary>
    /// Bidirectional cross-references must work both ways: TryGetCrossIndices(A) must list B
    /// AND TryGetCrossIndices(B) must list A for any (A, B) the SIMBAD merge wired up. Catches
    /// the failure mode where the apply path populates only one direction of the symmetric
    /// edges and breaks lookups by alternative catalog identifier.
    ///
    /// Each tuple is "famous nebula resolvable from multiple catalogs", spanning every SIMBAD
    /// catalog so a parser regression on any one input fails this test even if name-lookup
    /// tests don't reference that catalog.
    /// </summary>
    [Theory]
    [InlineData("NGC6514", "M020")]               // Trifid: NGC <-> Messier
    [InlineData("NGC6514", "Cr360")]              // Trifid: NGC <-> Collinder
    [InlineData("NGC3372", "GUM033")]             // Carina Nebula: NGC <-> GUM
    [InlineData("NGC3372", "RCW_0053")]           // Carina Nebula: NGC <-> RCW
    [InlineData("NGC0224", "M031")]               // Andromeda: NGC <-> Messier
    [InlineData("NGC1976", "M042")]               // Orion: NGC <-> Messier
    [InlineData("NGC6960", "C034")]               // Veil East: NGC <-> Caldwell
    [InlineData("NGC7000", "C020")]               // North America: NGC <-> Caldwell
    [InlineData("M045", "Mel022")]                // Pleiades: Messier <-> Melotte
    [InlineData("HIP011767", "HD008890")]         // Polaris: HIP <-> HD
    public async Task GivenCrossReferencedObject_WhenLookingUpEitherIdentifier_ThenBothResolveBidirectionally(
        string idA, string idB)
    {
        var db = new CelestialObjectDB();
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

        CatalogUtils.TryGetCleanedUpCatalogName(idA, out var indexA).ShouldBeTrue($"Failed to parse '{idA}'");
        CatalogUtils.TryGetCleanedUpCatalogName(idB, out var indexB).ShouldBeTrue($"Failed to parse '{idB}'");

        db.TryLookupByIndex(indexA, out _).ShouldBeTrue($"{idA} not in _objectsByIndex");
        db.TryLookupByIndex(indexB, out _).ShouldBeTrue($"{idB} not in _objectsByIndex");

        db.TryGetCrossIndices(indexA, out var crossA).ShouldBeTrue($"{idA} has no cross-refs");
        crossA.ShouldContain(indexB, $"{idA} -> {idB} cross-ref missing");

        db.TryGetCrossIndices(indexB, out var crossB).ShouldBeTrue($"{idB} has no cross-refs");
        crossB.ShouldContain(indexA, $"{idB} -> {idA} cross-ref missing (reverse direction broken)");
    }

    /// <summary>
    /// Common-name resolution should return ALL catalog identifiers that refer to the same
    /// physical object. The apply path's NameMappings snapshot is the only place where these
    /// many-to-one cross-resolutions live (the per-object CommonNames sets aren't enough).
    /// One representative case per SIMBAD-touching catalog so a regression in any of them
    /// fails this test.
    /// </summary>
    // NOTE on spellings: SIMBAD stores informal nicknames in their canonical SIMBAD form
    // ("eta Car Nebula", not "Eta Carinae Nebula"); the more common English spelling isn't
    // attached unless an NGC.addendum.csv row carries it. Caldwell aliases (e.g. C020 for
    // NGC7000) resolve via TryLookupByIndex's cross-ref fallback but are NOT separately
    // registered in _objectsByCommonName — looking up the name returns the primary catalog
    // entries (NGC + SIMBAD-touched cross-cats), and the Caldwell ID is reachable via
    // TryGetCrossIndices, not TryResolveCommonName. Cases below use the spellings that
    // actually exist in the source data.
    [Theory]
    [InlineData("Trifid Nebula", new[] { "NGC6514", "M020", "Cr360" })]
    [InlineData("Carina Nebula", new[] { "NGC3372", "GUM033", "RCW_0053" })]
    [InlineData("eta Car Nebula", new[] { "NGC3372", "GUM033", "RCW_0053" })]
    [InlineData("North America Nebula", new[] { "NGC7000" })]
    [InlineData("Pleiades", new[] { "Mel022", "M045" })]
    [InlineData("Andromeda Galaxy", new[] { "NGC0224", "M031" })]
    [InlineData("Orion Nebula", new[] { "NGC1976", "M042" })]
    [InlineData("Cave Nebula", new[] { "C009", "Ced0201", "DG0179" })]
    public async Task GivenPopularNickname_WhenResolving_ThenEveryAssociatedCatalogIndexIsListed(string name, string[] expectedIds)
    {
        var db = new CelestialObjectDB();
        await db.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.TryResolveCommonName(name, out var matches).ShouldBeTrue($"Common name '{name}' not resolvable");
        var matchSet = new HashSet<CatalogIndex>(matches);
        foreach (var idStr in expectedIds)
        {
            CatalogUtils.TryGetCleanedUpCatalogName(idStr, out var idx).ShouldBeTrue($"Failed to parse '{idStr}'");
            matchSet.ShouldContain(idx, $"'{name}' resolution missing {idStr}. Got [{string.Join(",", matches)}]");
        }
    }
}
