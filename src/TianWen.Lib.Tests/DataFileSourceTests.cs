using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The library reads its data files through <see cref="IDataFileSource"/> (#2161), so a file a trimmed build no
/// longer embeds can come from the host instead: from memory, a disk, the web. Shown here on the catalogue
/// database, the largest reader: it reads the same sky whether its files are embedded, fetched slowly, or
/// supplied by a host for the ones the assembly lacks, and it names a file nobody has.
/// </summary>
public class DataFileSourceTests
{
    /// <summary>The embedded files with some of them taken away, as a build that trimmed a group reads.</summary>
    private sealed class Without(IDataFileSource inner, params string[] missing) : IDataFileSource
    {
        public ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
            => missing.Contains(fileName) ? new ValueTask<Stream?>((Stream?)null) : inner.OpenAsync(fileName, cancellationToken);

        public Stream? OpenIfAtHand(string fileName) => missing.Contains(fileName) ? null : inner.OpenIfAtHand(fileName);
    }

    /// <summary>
    /// A source that has nothing at hand and answers only after yielding, with a stream that cannot seek and
    /// does not know its length, as one that fetches over the web does. It records what it was asked for, in order.
    /// </summary>
    private sealed class Fetched(IDataFileSource inner) : IDataFileSource
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        public async ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
        {
            Asked.Enqueue(fileName);
            await Task.Yield();
            return await inner.OpenAsync(fileName, cancellationToken) is { } stream ? new ForwardOnly(stream) : null;
        }

        public Stream? OpenIfAtHand(string fileName) => null;
    }

    /// <summary>A response body: read forward, no length, no position.</summary>
    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static async Task<byte[]> EmbeddedBytesAsync(string fileName, CancellationToken ct)
    {
        using var stream = (await DataFiles.Embedded.OpenAsync(fileName, ct)).ShouldNotBeNull($"{fileName} is embedded in the test build");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    private static CatalogIndex Index(string name)
    {
        CatalogUtils.TryGetCleanedUpCatalogName(name, out var index).ShouldBeTrue(name);
        return index;
    }

    [Theory]
    [InlineData("NGC.gs.gz", true)]
    [InlineData("tyc2_gsc_bounds.bin.lz", true)]
    [InlineData("hd_to_tyc.bin", true)]
    // A file is found by its whole name: no file is called "tyc.bin", though two names end in it.
    [InlineData("tyc.bin", false)]
    [InlineData("no-such-file.gz", false)]
    public async Task TheEmbeddedSourceFindsAFileByItsWholeName(string fileName, bool there)
    {
        var ct = TestContext.Current.CancellationToken;
        using var awaited = await DataFiles.Embedded.OpenAsync(fileName, ct);
        using var atHand = DataFiles.Embedded.OpenIfAtHand(fileName);

        (awaited is not null).ShouldBe(there);
        (atHand is not null).ShouldBe(there);
    }

    [Fact]
    public async Task TheFirstSourceThatHasAFileAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        var mine = new InMemoryDataFiles();
        mine.Add("mine.bin", [1, 2, 3]);
        var source = DataFiles.FirstOf(new Without(DataFiles.Embedded, "NGC.gs.gz"), mine);

        using var supplied = (await source.OpenAsync("mine.bin", ct)).ShouldNotBeNull();
        supplied.Length.ShouldBe(3);
        using var embedded = (await source.OpenAsync("HR.gs.gz", ct)).ShouldNotBeNull();
        (await source.OpenAsync("NGC.gs.gz", ct)).ShouldBeNull("neither source has it");
        source.OpenIfAtHand("mine.bin").ShouldNotBeNull().Dispose();
    }

    [Fact(Timeout = 120_000)]
    public async Task TheCatalogueReadsTheSameSkyThroughASourceThatFetchesEveryFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = await SharedCatalogDB.InitAsync(ct);

        var fetched = new Fetched(DataFiles.Embedded);
        var db = new CelestialObjectDB(fetched);
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);

        db.AllObjectIndices.Count.ShouldBe(reference.AllObjectIndices.Count);
        db.Tycho2StarCount.ShouldBe(reference.Tycho2StarCount);
        db.TryLookupByIndex(Index("M 42"), out var m42).ShouldBeTrue();
        reference.TryLookupByIndex(Index("M 42"), out var expected).ShouldBeTrue();
        m42.RA.ShouldBe(expected.RA);
        db.TryGetShape(Index("B 33"), out _).ShouldBe(reference.TryGetShape(Index("B 33"), out _));
        db.TryGetArticle(Index("NGC 1976"), out _).ShouldBe(reference.TryGetArticle(Index("NGC 1976"), out _));

        // The source was asked for a file of each kind of reader. That no reader went round it is the source
        // guard's to say (NothingInTheLibraryOpensAnEmbeddedFileBehindTheSource), not this list's.
        fetched.Asked.ShouldContain("NGC.gs.gz");
        fetched.Asked.ShouldContain("simbad_merge.bin.gz");
        fetched.Asked.ShouldContain("tyc2.bin");
        fetched.Asked.ShouldContain(Tycho2RegionSelector.BoundsFileName);
        fetched.Asked.ShouldContain(ObjectArticleTable.FileName);
    }

    [Fact(Timeout = 120_000)]
    public async Task TheCrossReferenceHashAsksFirstForTheInputNoBuildCarries()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = new Fetched(DataFiles.Embedded);
        var db = new CelestialObjectDB(fetched);
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        // The hash cannot be computed without tyc2.bin.lz, so nothing is fetched for it: each catalogue is asked
        // for by the SIMBAD hash and no second time by this one. (NGC is also read by its own reader.)
        db.LastInitPhaseTimings.Select(p => p.Phase).ShouldContain("hd-hip-cross-snapshot:unverified-no-tyc2-lz");
        fetched.Asked.Count(n => n == "tyc2.bin.lz").ShouldBe(1);
        fetched.Asked.Count(n => n == "Barnard.gs.gz").ShouldBe(1);
        fetched.Asked.Count(n => n == "vdB.gs.gz").ShouldBe(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task WithoutItsSnapshotTheCrossReferencesAreBuiltFromTycho2()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = await SharedCatalogDB.InitAsync(ct);
        var sirius = Index("HD 48915");
        reference.TryGetCrossIndices(sirius, out var expected).ShouldBeTrue("the snapshot's own answer");

        // A full build, less the snapshot: the live compute has Tycho-2 in hand and runs. (It used to ask
        // whether tyc2.bin.lz was embedded, which no build embeds, and skipped it.)
        var db = new CelestialObjectDB(new Without(DataFiles.Embedded, "hd_hip_cross.bin.gz"));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        var phases = db.LastInitPhaseTimings.Select(p => p.Phase).ToList();
        phases.ShouldContain("hd-hip-cross-snapshot:missing");
        phases.ShouldNotContain("hd-hip-cross:skipped-lightweight");
        db.AllObjectIndices.Count.ShouldBe(reference.AllObjectIndices.Count);
        db.TryGetCrossIndices(sirius, out var built).ShouldBeTrue();
        built.ShouldBe(expected, ignoreOrder: true);

        // The same with no Tycho-2 either is the case that IS skipped, and it shows what the count above tells:
        // the HD stars the compute adds are not there.
        var bare = new CelestialObjectDB(new Without(DataFiles.Embedded, "hd_hip_cross.bin.gz", "tyc2.bin", "tyc2.bin.lz"));
        await bare.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        bare.LastInitPhaseTimings.Select(p => p.Phase).ShouldContain("hd-hip-cross:skipped-lightweight");
        bare.AllObjectIndices.Count.ShouldBeLessThan(reference.AllObjectIndices.Count);
    }

    [Fact(Timeout = 120_000)]
    public async Task ACatalogueTheSnapshotWasNotBakedFromIsMergedLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = await SharedCatalogDB.InitAsync(ct);

        // The host serves an older Barnard beside this build's snapshot: the same file less its last record.
        var barnard = Gunzip(await EmbeddedBytesAsync("Barnard.gs.gz", ct));
        var lastRecord = barnard.AsSpan(0, barnard.Length - 1).LastIndexOf(AsciiRecordReader.GroupSeparator) + 1;
        lastRecord.ShouldBeGreaterThan(0);
        var host = new InMemoryDataFiles();
        host.Add("Barnard.gs.gz", Gzip(barnard.AsSpan(0, lastRecord)));

        var db = new CelestialObjectDB(DataFiles.FirstOf(host, DataFiles.Embedded));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        var phases = db.LastInitPhaseTimings.Select(p => p.Phase).ToList();
        phases.ShouldContain("simbad-snapshot:stale");
        phases.ShouldNotContain("simbad-snapshot:applied");
        db.TryLookupByIndex(Index("M 42"), out _).ShouldBeTrue();
        db.AllObjectIndices.Count.ShouldBeLessThanOrEqualTo(reference.AllObjectIndices.Count);
    }

    [Fact]
    public async Task TheTileSelectorsBoundsComeEmbeddedOrFromTheSource()
    {
        Tycho2RegionSelector.FromEmbeddedBounds().ShouldNotBeNull("the test build embeds the bounds");
        (await Tycho2RegionSelector.FromBoundsAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var source = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Gzip(ReadOnlySpan<byte> raw)
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(raw);
        }

        return packed.ToArray();
    }

    [Fact(Timeout = 120_000)]
    public async Task AFileTheAssemblyLacksIsReadFromTheHost()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = await SharedCatalogDB.InitAsync(ct);
        string[] taken = ["Barnard.gs.gz", "NGC.gs.gz", ObjectArticleTable.FileName];

        var host = new InMemoryDataFiles();
        foreach (var name in taken)
        {
            host.Add(name, await EmbeddedBytesAsync(name, ct));
        }

        var db = new CelestialObjectDB(DataFiles.FirstOf(new Without(DataFiles.Embedded, taken), host));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        db.AllObjectIndices.Count.ShouldBe(reference.AllObjectIndices.Count);
        db.TryLookupByIndex(Index("B 33"), out _).ShouldBeTrue();
        db.TryLookupByIndex(Index("NGC 7000"), out _).ShouldBeTrue();
        db.TryGetArticle(Index("NGC 1976"), out _).ShouldBe(reference.TryGetArticle(Index("NGC 1976"), out _));
    }

    [Fact(Timeout = 120_000)]
    public async Task ACatalogueNobodyHasIsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new CelestialObjectDB(new Without(DataFiles.Embedded, "Barnard.gs.gz"));

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            async () => await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct));

        failure.Message.ShouldContain("Barnard.gs.gz");
        failure.Message.ShouldContain("DataFiles.Source");
    }

    /// <summary>
    /// A reader that opens a manifest resource itself reads only what is embedded, so a file a host supplies
    /// never reaches it, and nothing says so until a trimmed build runs. The two snapshot hashers keep an
    /// overload over an assembly for the bake tools, which hash a build's own resources on purpose.
    /// </summary>
    [Fact]
    public void NothingInTheLibraryOpensAnEmbeddedFileBehindTheSource()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            return;
        }

        string[] allowed = ["DataFiles.cs", "HdHipCrossSnapshot.cs", "SimbadMergeSnapshot.cs"];
        var call = "GetManifestResource" + "Stream(";
        var offenders = FileEnumeration.EnumerateFiles(Path.Combine(root, "TianWen.Lib"), [".cs"], recursive: true)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !allowed.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadLines(f).Any(line => line.Contains(call, StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.ShouldBeEmpty("reads an embedded resource directly; ask DataFiles.Source (or the source it was given) for the file by name: ");
    }

    [Fact(Timeout = 120_000)]
    public async Task WithoutTheArticleTableTheCatalogueStillLoadsAndLinksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = new CelestialObjectDB(new Without(DataFiles.Embedded, ObjectArticleTable.FileName));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        db.TryLookupByIndex(Index("M 42"), out _).ShouldBeTrue();
        db.TryGetArticle(Index("NGC 1976"), out _).ShouldBeFalse();
    }
}
