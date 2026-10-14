using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas's runtime OPAL upgrade (docs/plans/atlas-planet-detail.md, "A4: the textured disc"): a failed fetch keeps the atlas and is
/// not asked again until <see cref="PlanetTextures.RetryCooldown"/> has passed; a fetch that lands replaces the giant's map, says so in
/// its credit and bumps the version a sprite is keyed on. No network: the store is a fake.
/// </summary>
public sealed class PlanetTexturesTests
{
    private sealed class FakeStore(Func<OpalColourMap, byte[]?> answer) : IPlanetMapStore
    {
        public int Calls;

        public Task<byte[]?> GetAsync(OpalColourMap map, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(map));
        }
    }

    private static byte[] AtlasBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName));

    [Fact(Timeout = 30_000)]
    public async Task AFailedFetchKeepsTheAtlasAndIsCooledDown()
    {
        var now = new DateTimeOffset(2026, 10, 14, 20, 0, 0, TimeSpan.Zero);
        var store = new FakeStore(_ => throw new HttpRequestException("no network in a test"));
        var textures = new PlanetTextures(() => now) { AtlasPath = null, Store = store };
        textures.SetAtlas(AtlasBytes());

        textures.TryGet(CatalogIndex.Jupiter, out var map, out var credit).ShouldBeTrue();
        await textures.LastUpgrade.ShouldNotBeNull().WaitAsync(TestContext.Current.CancellationToken);
        map.ShouldNotBeNull();
        credit.ShouldBe(PlanetTextureAtlas.Credit(CatalogIndex.Jupiter));

        // Asked every frame afterwards: the atlas is still drawn, and the server is not asked again inside the cooldown.
        for (var frame = 0; frame < 100; frame++)
        {
            textures.TryGet(CatalogIndex.Jupiter, out _, out credit).ShouldBeTrue();
        }
        now += PlanetTextures.RetryCooldown - TimeSpan.FromMinutes(1);
        textures.TryGet(CatalogIndex.Jupiter, out _, out _).ShouldBeTrue();
        textures.UpgradeAttempts.ShouldBe(1);
        store.Calls.ShouldBe(1);
        credit.ShouldBe(PlanetTextureAtlas.Credit(CatalogIndex.Jupiter), "the atlas is kept");

        now += TimeSpan.FromMinutes(2);
        textures.TryGet(CatalogIndex.Jupiter, out _, out _).ShouldBeTrue();
        await textures.LastUpgrade.ShouldNotBeNull().WaitAsync(TestContext.Current.CancellationToken);
        textures.UpgradeAttempts.ShouldBe(2, "asked again once the cooldown has passed");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALandedFetchReplacesTheGiantsMapAndBumpsTheVersion()
    {
        // A 4 x 2 8-bit RGB map, one colour throughout.
        var pixels = new byte[4 * 2 * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2]) = ((byte)200, (byte)100, (byte)50);
        }
        using var tiff = new MemoryStream();
        await using (var writer = TiffWriter.Create(tiff))
        {
            await writer.AddPageAsync(pixels, 4, 2, new TiffPageOptions
            {
                SamplesPerPixel = 3,
                BitsPerSample = 8,
                Photometric = TiffPhotometric.Rgb,
                SampleFormat = TiffSampleFormat.Uint,
            });
        }
        var bytes = tiff.ToArray();
        var store = new FakeStore(map => map.Body == CatalogIndex.Saturn ? bytes : null);
        var textures = new PlanetTextures(() => default) { AtlasPath = null, Store = store };
        textures.SetAtlas(AtlasBytes());
        var before = textures.Version;

        textures.TryGet(CatalogIndex.Saturn, out _, out _).ShouldBeTrue();
        await textures.LastUpgrade.ShouldNotBeNull().WaitAsync(TestContext.Current.CancellationToken);
        textures.TryGet(CatalogIndex.Saturn, out var map, out var credit).ShouldBeTrue();

        textures.Version.ShouldBeGreaterThan(before);
        credit.ShouldBe("OPAL (HST) 2025b, colour rendering");
        map.Red.Sample(0, 0).ShouldBe(200 / 255.0, 1e-6);
        textures.SetAtlas(AtlasBytes());
        textures.TryGet(CatalogIndex.Saturn, out _, out credit).ShouldBeTrue();
        credit.ShouldBe("OPAL (HST) 2025b, colour rendering", "a later atlas never undoes the upgrade");
        // A body OPAL does not observe never asks.
        textures.TryGet(CatalogIndex.Mars, out _, out _).ShouldBeTrue();
        store.Calls.ShouldBe(1);
    }
}
