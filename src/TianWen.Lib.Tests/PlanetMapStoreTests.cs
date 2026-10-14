using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The desktop's OPAL map cache (<see cref="PlanetMapStore"/>, docs/plans/atlas-planet-detail.md, "A4: the textured disc"): it keeps
/// only a map that decodes. A 200 answered with something else (a maintenance page) is never written, so it cannot be served from the
/// cache for good, and a cached file that no longer decodes is deleted, so the next ask after the cooldown fetches again. No network:
/// the client is a fake.
/// </summary>
public sealed class PlanetMapStoreTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();
    private readonly DirectoryInfo _cache;

    public PlanetMapStoreTests()
    {
        _cache = _folders.Create("tianwen-planet-maps-");
    }

    public void Dispose() => _folders.Dispose();

    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage MaintenancePage() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><body>MAST is down for maintenance</body></html>", Encoding.UTF8, "text/html"),
    };

    [Fact]
    public async Task AnHtmlPageAnsweredWithA200IsNeverCachedAndTheAtlasIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeHandler(MaintenancePage);
        using var http = new HttpClient(handler);
        var store = new PlanetMapStore(http, () => _cache, NullLogger.Instance);
        OpalColourMaps.TryFor(CatalogIndex.Jupiter, out var jupiter).ShouldBeTrue();

        (await store.GetAsync(jupiter, ct)).ShouldBeNull("a body that is not a map is no map");
        _cache.GetFiles().ShouldBeEmpty("nothing that does not decode is written");

        var textures = new PlanetTextures(() => default) { AtlasPath = null, Store = store };
        textures.SetAtlas(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName), ct));
        textures.TryGet(CatalogIndex.Jupiter, out _, out _).ShouldBeTrue();
        await textures.LastUpgrade.ShouldNotBeNull().WaitAsync(ct);
        textures.TryGet(CatalogIndex.Jupiter, out _, out var credit).ShouldBeTrue();
        credit.ShouldBe(PlanetTextureAtlas.Credit(CatalogIndex.Jupiter), "the atlas is kept");
        handler.Requests.ShouldBe(2, "the store asked the server each time, never answering from a cached page");
    }

    [Fact]
    public async Task ACachedFileThatDoesNotDecodeIsDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        OpalColourMaps.TryFor(CatalogIndex.Saturn, out var saturn).ShouldBeTrue();
        // What an older build left behind: the maintenance page under the map's own cache name.
        var cached = Path.Combine(_cache.FullName, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(saturn.Url)))[..32] + ".tif");
        await File.WriteAllTextAsync(cached, "<html>maintenance</html>", ct);
        var handler = new FakeHandler(MaintenancePage);
        using var http = new HttpClient(handler);
        var store = new PlanetMapStore(http, () => _cache, NullLogger.Instance);

        (await store.GetAsync(saturn, ct)).ShouldBeNull();

        File.Exists(cached).ShouldBeFalse("a cached file that does not decode is deleted, so the next ask fetches again");
    }
}
