using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The session ledger's fingerprint answers "would the same inputs and the same recipe make the
/// same outputs", and these pin what moves it and what does not. A resume used to ask whether the
/// files were there, which is neither question.
/// </summary>
public class DatasetSessionLedgerTests
{
    [Fact]
    public async Task TheFingerprintMovesWithALightsBytesAndWithTheRecipeAndNotWithTime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tw-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var a = Path.Combine(root, "L_001.fits");
            var b = Path.Combine(root, "L_002.fits");
            await File.WriteAllBytesAsync(a, new byte[2880], ct);
            await File.WriteAllBytesAsync(b, new byte[2880], ct);
            var session = Session(root, [a, b]);

            var before = DatasetSessionLedger.FingerprintOf(session, "cal:1", "recipe:1");
            DatasetSessionLedger.FingerprintOf(session, "cal:1", "recipe:1")
                .ShouldBe(before, "the same inputs fingerprint the same, run to run");

            // A light re-written (a curation pass re-typing a card) moves size or mtime, and that is
            // what the fingerprint watches: the archive digest store's own "unchanged" rule.
            File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddMinutes(5));
            var touched = DatasetSessionLedger.FingerprintOf(session, "cal:1", "recipe:1");
            touched.ShouldNotBe(before, "a touched light is a changed input");

            // The calibration library and the recipe each move it on their own.
            DatasetSessionLedger.FingerprintOf(session, "cal:2", "recipe:1").ShouldNotBe(touched);
            DatasetSessionLedger.FingerprintOf(session, "cal:1", "recipe:2").ShouldNotBe(touched);

            // Order of the lights does not: a session enumerated in another order is the same session.
            DatasetSessionLedger.FingerprintOf(Session(root, [b, a]), "cal:1", "recipe:1").ShouldBe(touched);

            // A light that is gone fingerprints as gone, not as an exception: a deleted light changes
            // the session the way an added one does.
            File.Delete(b);
            DatasetSessionLedger.FingerprintOf(session, "cal:1", "recipe:1").ShouldNotBe(touched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheLedgerRoundTripsLastEntryPerSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tw-ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await DatasetSessionLedger.AppendAsync(path, new DatasetSessionLedger.SessionLedgerEntry("s1", "fp-old", 1, DateTimeOffset.UnixEpoch, 10, "tiles/s1"), ct);
            await DatasetSessionLedger.AppendAsync(path, new DatasetSessionLedger.SessionLedgerEntry("s2", "fp-2", 1, DateTimeOffset.UnixEpoch, 20, "tiles/s2"), ct);
            await DatasetSessionLedger.AppendAsync(path, new DatasetSessionLedger.SessionLedgerEntry("s1", "fp-new", 1, DateTimeOffset.UnixEpoch, 12, "tiles/s1"), ct);

            var read = await DatasetSessionLedger.ReadAsync(path, cancellationToken: ct);

            read.Count.ShouldBe(2);
            read["s1"].Fingerprint.ShouldBe("fp-new", "the last record per session wins, like every other store");
            read["s1"].TileCount.ShouldBe(12);
            read["s2"].TileDirRelative.ShouldBe("tiles/s2");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ImagingSession Session(string dir, string[] lightPaths)
    {
        var lights = ImmutableArray.CreateBuilder<FrameInfo>(lightPaths.Length);
        foreach (var path in lightPaths)
        {
            lights.Add(new FrameInfo(path, 16, 16, 1, BitDepth.Int16, new ImageMeta { Instrument = "TestCam" }));
        }
        return new ImagingSession(dir, "night", "TestCam", "Target", "", lights.ToImmutable());
    }
}
