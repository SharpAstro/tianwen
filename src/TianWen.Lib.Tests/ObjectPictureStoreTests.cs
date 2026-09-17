using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How a picture is addressed on Wikimedia (<see cref="ObjectArticleImage.ThumbnailUrl"/>) and how the desktop
/// fetches, caches and decodes it (<see cref="ObjectPictureStore"/>).
/// </summary>
public sealed class ObjectPictureStoreTests : IDisposable
{
    private readonly DirectoryInfo _cache = Directory.CreateTempSubdirectory("tianwen-object-pictures-");

    public void Dispose() => _cache.Delete(recursive: true);

    private static ObjectArticleImage Image(string fileName)
        => new ObjectArticleImage(fileName,
            Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(fileName.Replace(' ', '_'))))[..2],
            "CC BY 4.0", "Someone", "", AttributionRequired: true, Width: 4000, Height: 3000);

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    [Theory]
    [InlineData(1, 250)]
    [InlineData(330, 330)]
    [InlineData(331, 500)]
    [InlineData(640, 960)]  // 640 itself is refused by the image servers
    [InlineData(5000, 1920)]
    public void AWidthRoundsUpToAStandardOne(int pixels, int expected)
        => ObjectArticleImage.StandardWidthFor(pixels).ShouldBe(expected);

    [Fact]
    public void TheThumbnailUrlIsTheImageServersOwnPath()
    {
        // Checked against upload.wikimedia.org on 2026-09-18: this URL answers 200 image/jpeg.
        var url = Image("Orion Nebula - Hubble 2006 mosaic 18000.jpg").ThumbnailUrl(400);

        url.ShouldBe("https://upload.wikimedia.org/wikipedia/commons/thumb/f/f3/"
            + "Orion_Nebula_-_Hubble_2006_mosaic_18000.jpg/500px-Orion_Nebula_-_Hubble_2006_mosaic_18000.jpg");
    }

    [Fact]
    public void ATiffIsAskedForAsTheJpegOfItsFirstPage()
    {
        var url = Image("Noao-m97.tif").ThumbnailUrl(500);

        url.ShouldEndWith("/Noao-m97.tif/lossy-page1-500px-Noao-m97.tif.jpg");
    }

    [Fact]
    public void AParenthesisInTheFileNameIsEscapedInBothPlaces()
    {
        var url = Image("Andromeda Galaxy (with h-alpha).jpg").ThumbnailUrl(250);

        url.ShouldEndWith("/Andromeda_Galaxy_%28with_h-alpha%29.jpg/250px-Andromeda_Galaxy_%28with_h-alpha%29.jpg");
    }

    [Fact]
    public async Task APictureIsFetchedOnceAndThenServedFromTheCache()
    {
        byte[] rgba = [10, 20, 30, 255, 40, 50, 60, 255];
        var png = PngWriter.Encode(rgba, 2, 1);
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        using var http = new HttpClient(handler);
        var store = new ObjectPictureStore(http, () => _cache, NullLogger.Instance);
        var image = Image("Crab Nebula.jpg");
        var ct = TestContext.Current.CancellationToken;

        var first = await store.GetAsync(image, 500, ct);
        var second = await store.GetAsync(image, 500, ct);

        handler.Requests.ShouldBe(1, "the second request must come from the disk cache");
        first.ShouldNotBeNull().Rgba.ShouldBe(rgba);
        second.ShouldNotBeNull().Width.ShouldBe(2);
        _cache.GetFiles().Length.ShouldBe(1);

        // Another width is another picture.
        await store.GetAsync(image, 1280, ct);
        handler.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task ARefusedPictureIsNullAndNothingIsCached()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var store = new ObjectPictureStore(http, () => _cache, NullLogger.Instance);

        (await store.GetAsync(Image("Missing.jpg"), 500, TestContext.Current.CancellationToken)).ShouldBeNull();
        _cache.GetFiles().ShouldBeEmpty();
    }

    [Fact]
    public void AFormatThatIsNeitherPngNorJpegDecodesToNull()
        => ObjectPictureStore.Decode("GIF89a"u8).ShouldBeNull();
}
