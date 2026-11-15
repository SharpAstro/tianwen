using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    /// A source that has nothing at hand and answers only after yielding, as one that fetches over the web does,
    /// and counts what it was asked for.
    /// </summary>
    private sealed class Fetched(IDataFileSource inner) : IDataFileSource
    {
        public ConcurrentDictionary<string, int> Asked { get; } = new(StringComparer.Ordinal);

        public async ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
        {
            Asked.AddOrUpdate(fileName, 1, static (_, n) => n + 1);
            await Task.Yield();
            return await inner.OpenAsync(fileName, cancellationToken);
        }

        public Stream? OpenIfAtHand(string fileName) => null;
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

        // It was asked, and nothing else read the files behind its back.
        fetched.Asked.Keys.ShouldContain("NGC.gs.gz");
        fetched.Asked.Keys.ShouldContain("simbad_merge.bin.gz");
        fetched.Asked.Keys.ShouldContain(Tycho2RegionSelector.BoundsFileName);
        fetched.Asked.Keys.ShouldContain(ObjectArticleTable.FileName);
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
