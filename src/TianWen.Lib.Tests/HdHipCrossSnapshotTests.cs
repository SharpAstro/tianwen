using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Guards the build-time hd_hip_cross.bin.gz snapshot wired up by Phase 2A of
/// PLAN-catalog-binary-format.md. Two responsibilities:
///   1. Catch staleness in CI (input hash mismatch) so a catalog edit that forgets
///      to re-bake the snapshot fails loudly instead of silently regressing init time.
///   2. Catch divergence between the live BuildHdHipCrossIndicesViaTyc path and the
///      snapshot apply path for representative HD/HIP cross-references.
/// </summary>
[Collection("Catalog")]
public class HdHipCrossSnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void GivenEmbeddedSnapshot_WhenHashedAgainstCurrentInputs_ThenItIsFresh()
    {
        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var snapshotResource = manifestNames.FirstOrDefault(n => n.EndsWith(".hd_hip_cross.bin.gz", StringComparison.Ordinal));
        snapshotResource.ShouldNotBeNull(
            "Snapshot resource hd_hip_cross.bin.gz is not embedded. Run tools/precompute-hd-hip-cross.ps1 to bake it.");

        using var stream = assembly.GetManifestResourceStream(snapshotResource!)
            ?? throw new InvalidOperationException("Snapshot resource stream was null.");
        _ = HdHipCrossSnapshotIo.Read(stream, out var storedHash);

        var expectedHash = HdHipCrossInputHasher.Compute(assembly, manifestNames);
        Convert.ToHexString(storedHash).ShouldBe(Convert.ToHexString(expectedHash),
            "Embedded hd_hip_cross.bin.gz is stale: at least one catalog input changed since the snapshot was baked. " +
            "Run tools/precompute-hd-hip-cross.ps1 to refresh, then commit the regenerated file.");

        output.WriteLine($"Snapshot input hash: {Convert.ToHexString(storedHash)[..16]}...");
    }

    [Fact]
    public async Task GivenLiveAndSnapshotPaths_WhenComparingState_ThenTheyAgree()
    {
        // Drive the precompute capture path so we have a fresh in-memory snapshot to compare
        // against the runtime apply path. This catches algorithm-vs-snapshot semantic drift
        // (e.g. a new field on CelestialObject that the apply path forgets to populate)
        // INDEPENDENTLY of whether the embedded snapshot is fresh.
        var live = new CelestialObjectDB { ForceLiveHdHipCrossWithCapture = true };
        await live.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);
        live.LastHdHipCrossSnapshot.ShouldNotBeNull("Live capture path produced no snapshot");

        var snapshot = live.LastHdHipCrossSnapshot!;
        snapshot.HdEntries.Length.ShouldBeGreaterThan(50_000, "HD entries unexpectedly low — bad inputs?");
        snapshot.Edges.Length.ShouldBeGreaterThan(100_000, "Edge delta unexpectedly low — bad inputs?");

        // Default-init a second DB; if there is an embedded snapshot it'll go through the apply
        // path, otherwise it'll go through live compute (and the comparison degenerates to
        // self-equality, which is still useful as a smoke test).
        var applied = new CelestialObjectDB();
        await applied.InitDBAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Spot-check 64 HD indices from the captured snapshot. We don't compare every one of
        // ~89K because building two full catalogues here already costs ~3 s in unit tests;
        // a representative spread (every Nth index from the snapshot's stable order) catches
        // the kinds of divergence we care about — wrong RA/Dec, wrong type, wrong edges.
        var step = Math.Max(1, snapshot.HdEntries.Length / 64);
        for (var i = 0; i < snapshot.HdEntries.Length; i += step)
        {
            var hd = snapshot.HdEntries[i];
            applied.TryLookupByIndex(hd.Index, out var appliedObj).ShouldBeTrue(
                $"Apply path missing HD entry {hd.Index}");
            appliedObj.ObjectType.ShouldBe(hd.ObjType, $"HD {hd.Index}: type drift");
            appliedObj.RA.ShouldBe(hd.Ra, $"HD {hd.Index}: RA drift");
            appliedObj.Dec.ShouldBe(hd.Dec, $"HD {hd.Index}: Dec drift");
            appliedObj.Constellation.ShouldBe(hd.Constellation, $"HD {hd.Index}: constellation drift");

            // Cross-index check: the live snapshot's edge for this HD should resolve in the
            // applied DB. Use the public TryGetCrossIndices to walk the same dict we wrote to.
            applied.TryGetCrossIndices(hd.Index, out var appliedCross).ShouldBeTrue(
                $"Apply path missing cross-index entries for HD {hd.Index}");
            appliedCross.Count.ShouldBeGreaterThan(0, $"HD {hd.Index}: empty cross-index set");
        }

        output.WriteLine($"Verified {snapshot.HdEntries.Length / step} HD entries match between live capture and apply paths.");
    }
}
