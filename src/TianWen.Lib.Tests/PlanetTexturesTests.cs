using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Png;
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

    /// <summary>
    /// The atlas holds nine bodies, the Earth among them (#1573): two to a row, five rows, so 512 x 640, and the file beside the tests is
    /// that size and gives the Earth its map credited to NASA's Blue Marble. An atlas of the old size (eight bodies, 512 x 512) is refused
    /// whole, so a stale file leaves the bodies flat, never a map read from the wrong cell. The Sun (#1608) took the tenth cell with no
    /// change of size, so the nine-body atlas it replaces is the same size: its empty cell, all black, is no map, so the Sun gets none
    /// rather than a black one (since #1617 only the orrery then draws it flat; the sky map draws its dot and the info panel's slot stays
    /// dark until the atlas loads).
    /// </summary>
    [Fact]
    public void TheAtlasHoldsTheEarthAndAStaleAtlasIsRefused()
    {
        PlanetTextureAtlas.Layout.Length.ShouldBe(10);
        PlanetTextureAtlas.Layout.ShouldContain(CatalogIndex.Earth);
        (PlanetTextureAtlas.Width, PlanetTextureAtlas.Height).ShouldBe((512, 640));
        PngImageDecoder.TryReadInfo(AtlasBytes(), out var info).ShouldBeTrue();
        (info.Width, info.Height).ShouldBe((PlanetTextureAtlas.Width, PlanetTextureAtlas.Height), "the committed atlas is the layout's size");

        var textures = new PlanetTextures(() => DateTimeOffset.UnixEpoch) { AtlasPath = null };
        textures.SetAtlas(AtlasBytes());
        textures.TryGet(CatalogIndex.Earth, out var earth, out var credit).ShouldBeTrue();
        earth.ShouldNotBeNull().Width.ShouldBe(PlanetTextureAtlas.CellWidth);
        credit.ShouldBe(PlanetTextureAtlas.Credit(CatalogIndex.Earth));
        credit.ShouldContain("Blue Marble");
        credit.ShouldContain("public domain");

        var stale = new PlanetTextures(() => DateTimeOffset.UnixEpoch) { AtlasPath = null };
        stale.SetAtlas(PngWriter.EncodeRgb8(new byte[512 * 512 * 3], 512, 512));
        stale.Version.ShouldBe(0, "an atlas of the old size changes nothing");
        stale.TryGet(CatalogIndex.Earth, out _, out _).ShouldBeFalse();
        stale.TryGet(CatalogIndex.Jupiter, out _, out _).ShouldBeFalse();

        // The nine-body atlas: every cell as committed but the Sun's, black.
        PngImageDecoder.TryReadInfo(AtlasBytes(), out _).ShouldBeTrue();
        var rgba = new byte[PlanetTextureAtlas.Width * PlanetTextureAtlas.Height * 4];
        PngImageDecoder.TryDecodeIntoRgba8(AtlasBytes(), rgba).ShouldBeTrue();
        PlanetTextureAtlas.TryCell(CatalogIndex.Sol, out var sunX, out var sunY).ShouldBeTrue();
        var rgb = new byte[PlanetTextureAtlas.Width * PlanetTextureAtlas.Height * 3];
        for (var i = 0; i < PlanetTextureAtlas.Width * PlanetTextureAtlas.Height; i++)
        {
            var (x, y) = (i % PlanetTextureAtlas.Width, i / PlanetTextureAtlas.Width);
            var inSun = x >= sunX && x < sunX + PlanetTextureAtlas.CellWidth && y >= sunY && y < sunY + PlanetTextureAtlas.CellHeight;
            for (var c = 0; c < 3; c++)
            {
                rgb[(i * 3) + c] = inSun ? (byte)0 : rgba[(i * 4) + c];
            }
        }
        var nineBodies = new PlanetTextures(() => DateTimeOffset.UnixEpoch) { AtlasPath = null };
        nineBodies.SetAtlas(PngWriter.EncodeRgb8(rgb, PlanetTextureAtlas.Width, PlanetTextureAtlas.Height));
        nineBodies.TryGet(CatalogIndex.Earth, out _, out _).ShouldBeTrue();
        nineBodies.TryGet(CatalogIndex.Sol, out _, out _).ShouldBeFalse("an empty cell is no map: the Sun would be drawn black");
    }

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
            }, TestContext.Current.CancellationToken);
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
