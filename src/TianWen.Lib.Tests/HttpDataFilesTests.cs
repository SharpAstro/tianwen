using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="HttpDataFiles"/> is the source a web host stands behind the embedded files (#2161): a folder of
/// plain files with an index of what it carries. Asked through a handler held in memory, so no request leaves
/// the test.
/// </summary>
public class HttpDataFilesTests
{
    private static readonly Uri Folder = new Uri("https://app.example/tianwen/data/");

    /// <summary>
    /// A web folder in memory. A file mapped to null answers 503 until it is given bytes, and one named to
    /// <see cref="TimeOut"/> has its next requests end as the client's timeout does.
    /// </summary>
    private sealed class FolderHandler(Dictionary<string, byte[]?> initial) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte[]?> files = new(initial, StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, int> timeOuts = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> Requests { get; } = new();

        public void TimeOut(string name, int times) => timeOuts[name] = times;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri.ShouldNotBeNull();
            uri.AbsoluteUri.ShouldStartWith(Folder.AbsoluteUri, Case.Sensitive, "a request left the folder");
            var name = uri.AbsoluteUri[Folder.AbsoluteUri.Length..];
            Requests.Enqueue(name);
            if (timeOuts.AddOrUpdate(name, -1, (_, left) => left - 1) >= 0)
            {
                // What HttpClient throws for its own Timeout: a cancellation no caller asked for.
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));
            }

            var response = !files.TryGetValue(name, out var bytes) ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : bytes is null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return Task.FromResult(response);
        }

        public void Put(string name, byte[] bytes) => files[name] = bytes;
    }

    private static (HttpDataFiles Source, FolderHandler Handler, HttpClient Http) Serve(Dictionary<string, byte[]?> files, string? timesOutTwice = null)
    {
        var handler = new FolderHandler(files);
        if (timesOutTwice is not null)
        {
            handler.TimeOut(timesOutTwice, 2);
        }

        var http = new HttpClient(handler);
        return (new HttpDataFiles(http, Folder), handler, http);
    }

    private static byte[] Index(params string[] names) => System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", names) + "\n");

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var owned = stream;
        using var copy = new MemoryStream();
        await owned.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    [Fact(Timeout = 30_000)]
    public async Task AFileOnTheIndexIsFetchedAndOneThatIsNotIsAnsweredWithoutARequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, http) = Serve(new()
        {
            [HttpDataFiles.IndexFileName] = Index("a.bin", "b.bin"),
            ["a.bin"] = [1, 2, 3],
            ["b.bin"] = [4],
            ["not-listed.bin"] = [9],
        });
        using var owned = http;

        (await ReadAsync((await source.OpenAsync("a.bin", ct)).ShouldNotBeNull(), ct)).ShouldBe([1, 2, 3]);
        (await source.OpenAsync("not-listed.bin", ct)).ShouldBeNull();
        (await source.OpenAsync("tyc2.bin.lz", ct)).ShouldBeNull();

        // The whole index was fetched at once, each file once, and nothing off the index was asked for.
        (await source.OpenAsync("b.bin", ct)).ShouldNotBeNull().Dispose();
        handler.Requests.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["a.bin", "b.bin", HttpDataFiles.IndexFileName]);

        // Asked for again, a file comes from what was kept.
        (await ReadAsync((await source.OpenAsync("a.bin", ct)).ShouldNotBeNull(), ct)).ShouldBe([1, 2, 3]);
        source.OpenIfAtHand("a.bin").ShouldNotBeNull().Dispose();
        source.OpenIfAtHand("not-listed.bin").ShouldBeNull();
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFolderWithNoIndexCarriesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, http) = Serve(new() { ["a.bin"] = [1] });
        using var owned = http;

        (await source.OpenAsync("a.bin", ct)).ShouldBeNull();
        source.OpenIfAtHand("a.bin").ShouldBeNull();
        handler.Requests.ShouldBe([HttpDataFiles.IndexFileName]);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnIndexLineThatIsNotAPlainNameIsLeftOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, http) = Serve(new()
        {
            [HttpDataFiles.IndexFileName] = Index("a.bin", "../secret.bin", "https://elsewhere.example/x.bin", "sub/b.bin"),
            ["a.bin"] = [1],
        });
        using var owned = http;

        (await source.OpenAsync("a.bin", ct)).ShouldNotBeNull().Dispose();
        (await source.OpenAsync("../secret.bin", ct)).ShouldBeNull();
        handler.Requests.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["a.bin", HttpDataFiles.IndexFileName]);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFetchThatFailedNamesTheFileAndIsTriedAgainByTheNextReader()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, http) = Serve(new()
        {
            [HttpDataFiles.IndexFileName] = Index("a.bin"),
            ["a.bin"] = null,
        });
        using var owned = http;

        IOException? failure = null;
        try
        {
            using var never = await source.OpenAsync("a.bin", ct);
        }
        catch (IOException ex)
        {
            failure = ex;
        }

        failure.ShouldNotBeNull().Message.ShouldContain("a.bin");
        source.OpenIfAtHand("a.bin").ShouldBeNull();

        handler.Put("a.bin", [7]);
        (await ReadAsync((await source.OpenAsync("a.bin", ct)).ShouldNotBeNull(), ct)).ShouldBe([7]);
    }

    /// <summary>
    /// A timeout ends a task cancelled where a refused request ends it faulted. Kept as it came, the cancelled
    /// fetch was never tried again, and its reader was told of a cancellation it had not asked for. Twice, since
    /// the first request is the source's own, made before any reader: the reader's is the second, and it is told.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData("a.bin")]
    [InlineData(HttpDataFiles.IndexFileName)]
    public async Task ARequestThatTimedOutIsAFailedFetchAndIsTriedAgainByTheNextReader(string timesOut)
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, http) = Serve(new()
        {
            [HttpDataFiles.IndexFileName] = Index("a.bin"),
            ["a.bin"] = [7],
        }, timesOut);
        using var owned = http;

        IOException? failure = null;
        try
        {
            using var never = await source.OpenAsync("a.bin", ct);
        }
        catch (IOException ex)
        {
            failure = ex;
        }

        failure.ShouldNotBeNull().Message.ShouldContain(timesOut);
        failure.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        ct.IsCancellationRequested.ShouldBeFalse();

        (await ReadAsync((await source.OpenAsync("a.bin", ct)).ShouldNotBeNull(), ct)).ShouldBe([7]);
        handler.Requests.Count(r => r == timesOut).ShouldBe(3);
    }

    /// <summary>
    /// The article table and the outlines enrich the catalogue. Embedded they cannot fail to arrive; from a folder
    /// one dropped request must not end the planner, and the init says what it went without.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACatalogueFinishesItsInitWithoutAnOptionalFileThatCouldNotBeFetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var sky = SkyGroup();
        const string shapes = "LDN.shapes.json.lz";
        sky.ShouldContain(ObjectArticleTable.FileName);
        sky.ShouldContain(shapes);

        var files = await SkyFilesAsync(sky, ct);
        files[ObjectArticleTable.FileName] = null;
        files[shapes] = null;

        var (folder, _, http) = Serve(files);
        using var owned = http;
        var db = new CelestialObjectDB(DataFiles.FirstOf(new Without(DataFiles.Embedded, sky), folder));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        var phases = db.LastInitPhaseTimings.Select(p => p.Phase).ToArray();
        phases.ShouldContain("object-articles:unreadable");
        phases.ShouldContain("shapes:unreadable");
        CatalogUtils.TryGetCleanedUpCatalogName("M 42", out var m42).ShouldBeTrue();
        db.TryLookupByIndex(m42, out _).ShouldBeTrue();
        CatalogUtils.TryGetCleanedUpCatalogName("NGC 1976", out var ngc1976).ShouldBeTrue();
        db.TryGetArticle(ngc1976, out _).ShouldBeFalse();
    }

    /// <summary>A catalogue the database cannot do without is still a failed init, naming the file.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACatalogueThatCouldNotBeFetchedStillFailsTheInitNamingItsFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sky = SkyGroup();
        var files = await SkyFilesAsync(sky, ct);
        files["NGC.gs.gz"] = null;

        var (folder, _, http) = Serve(files);
        using var owned = http;
        var db = new CelestialObjectDB(DataFiles.FirstOf(new Without(DataFiles.Embedded, sky), folder));

        IOException? failure = null;
        try
        {
            await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);
        }
        catch (IOException ex)
        {
            failure = ex;
        }

        failure.ShouldNotBeNull().Message.ShouldContain("NGC.gs.gz");
    }

    /// <summary>The Sky group's file names, from the list the trimmer reads (<c>ILLink.Substitutions.xml</c>).</summary>
    private static string[] SkyGroup()
    {
        using var xml = typeof(Image).Assembly.GetManifestResourceStream("ILLink.Substitutions.xml").ShouldNotBeNull();
        return XDocument.Load(xml).Root.ShouldNotBeNull().Elements("assembly")
            .Single(a => (string?)a.Attribute("feature") == "TianWen.Lib.EmbeddedData.Sky")
            .Elements("resource").Select(r => ((string?)r.Attribute("name")).ShouldNotBeNull().Replace("TianWen.Lib.Astrometry.Catalogs.", ""))
            .ToArray();
    }

    private static async Task<Dictionary<string, byte[]?>> SkyFilesAsync(string[] sky, CancellationToken ct)
    {
        var files = new Dictionary<string, byte[]?> { [HttpDataFiles.IndexFileName] = Index(sky) };
        foreach (var name in sky)
        {
            using var stream = DataFiles.Embedded.OpenIfAtHand(name).ShouldNotBeNull(name);
            files[name] = await ReadAsync(stream, ct);
        }

        return files;
    }

    /// <summary>
    /// The web app as published: the assembly without its Sky group, the group's files in a folder, the folder's
    /// index written from the same list the trimmer reads (<c>ILLink.Substitutions.xml</c>).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCatalogueReadsItsSkyFromAFolderWhenTheAssemblyDoesNotCarryIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = await SharedCatalogDB.InitAsync(ct);

        var sky = SkyGroup();
        sky.ShouldContain("NGC.gs.gz");
        var files = await SkyFilesAsync(sky, ct);

        var (folder, handler, http) = Serve(files);
        using var owned = http;
        var db = new CelestialObjectDB(DataFiles.FirstOf(new Without(DataFiles.Embedded, sky), folder));
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        db.AllObjectIndices.Count.ShouldBe(reference.AllObjectIndices.Count);
        db.LastInitPhaseTimings.Select(p => p.Phase).ShouldContain("simbad-snapshot:applied");
        CatalogUtils.TryGetCleanedUpCatalogName("M 42", out var m42).ShouldBeTrue();
        db.TryLookupByIndex(m42, out _).ShouldBeTrue();
        CatalogUtils.TryGetCleanedUpCatalogName("NGC 1976", out var ngc1976).ShouldBeTrue();
        db.TryGetArticle(ngc1976, out _).ShouldBe(reference.TryGetArticle(ngc1976, out _));

        // Each file crossed the web once, however often the database asked for it.
        handler.Requests.Count.ShouldBe(sky.Length + 1);
        handler.Requests.Distinct().Count().ShouldBe(sky.Length + 1);
    }

    private sealed class Without(IDataFileSource inner, string[] missing) : IDataFileSource
    {
        public ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
            => missing.Contains(fileName) ? new ValueTask<Stream?>((Stream?)null) : inner.OpenAsync(fileName, cancellationToken);

        public Stream? OpenIfAtHand(string fileName) => missing.Contains(fileName) ? null : inner.OpenIfAtHand(fileName);
    }
}
