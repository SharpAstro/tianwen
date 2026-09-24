using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Alpaca;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The ImageBytes download streams the body into a rented buffer instead of letting HttpClient buffer it
/// and <c>ReadAsByteArrayAsync</c> copy it out: at least one payload-sized array per frame, 52 MB on a
/// 26 MP sub. Driven through a handler that serves a fixed body, so what is measured is the client alone.
/// </summary>
/// <remarks>
/// In the allocation collection because the measurement is process-wide across the awaits, and because
/// the timeout test's bound is only reliable with parallelization off.
/// </remarks>
[Collection("Allocations")]
public class AlpacaImageDownloadTests(ITestOutputHelper output)
{
    private const string BaseUrl = "http://camera.invalid";

    // 641 x 479 Int32 is 1.2 MB: past the chunked path's first 1 MiB buffer, so it has to grow.
    [Theory]
    [InlineData(true)]   // Content-Length declared: read exactly that much
    [InlineData(false)]  // chunked: the rented buffer grows until the body ends
    public async Task ThePayloadArrivesWholeWhetherOrNotItsLengthIsDeclared(bool declareLength)
    {
        var body = AlpacaImageBytesTests.BuildPayload(641, 479, AlpacaImageBytesTests.Int32, (x, y) => x * 1000 + y);
        var client = new AlpacaClient(new HttpClient(new FixedBodyHandler(body, declareLength)));

        using var payload = await client.GetImageArrayBytesAsync(BaseUrl, "camera", 0, "imagearray", TestContext.Current.CancellationToken);

        payload.Length.ShouldBe(body.Length);
        payload.Span.SequenceEqual(body).ShouldBeTrue();
        var channel = AlpacaImageBytes.DecodeChannel(payload.Span);
        (channel.Width, channel.Height).ShouldBe((641, 479));
        channel[478, 640].ShouldBe(640 * 1000 + 478f);
    }

    [Fact]
    public async Task ASteadyDownloadAllocatesNoPayloadSizedArray()
    {
        var body = AlpacaImageBytesTests.BuildPayload(640, 480, AlpacaImageBytesTests.Int32, (x, y) => x + y);
        var client = new AlpacaClient(new HttpClient(new FixedBodyHandler(body, declareLength: true)));
        var ct = TestContext.Current.CancellationToken;

        // The first download rents the buffer and JITs the path; its dispose is what the next one rents.
        (await client.GetImageArrayBytesAsync(BaseUrl, "camera", 0, "imagearray", ct)).Dispose();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        using (var payload = await client.GetImageArrayBytesAsync(BaseUrl, "camera", 0, "imagearray", ct))
        {
            payload.Length.ShouldBe(body.Length);
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        output.WriteLine($"{body.Length:N0}-byte payload: {allocated:N0} bytes allocated by the download");
        allocated.ShouldBeLessThan(body.Length / 4L,
            $"the body is read into a rented buffer, not a new array: {allocated:N0} bytes for a {body.Length:N0}-byte payload");
    }

    [Fact]
    public void APayloadHandsItsBufferBackOnceAndCannotBeReadAfter()
    {
        var payload = new ImageBytesPayload(ArrayPool<byte>.Shared.Rent(64), 44);
        payload.Span.Length.ShouldBe(44);

        payload.Dispose();
        payload.Dispose(); // a second return would hand one buffer to two renters

        Should.Throw<ObjectDisposedException>(() => payload.Span.Length);
    }

    /// <summary>
    /// HttpClient.Timeout stops at the headers once a body is streamed, so a server that sends its headers
    /// and then stalls mid-frame would hang the download for ever: the budget has to cover the body too,
    /// and running out of it has to read as a timeout, not a cancellation, since the caller cancelled
    /// nothing.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABodyThatStallsFailsAsATimeoutWithinTheClientsBudget()
    {
        var client = new AlpacaClient(new HttpClient(new StalledBodyHandler()) { Timeout = TimeSpan.FromMilliseconds(250) });
        var ct = TestContext.Current.CancellationToken;

        // Caught by hand: the exception a cancelled task carries is the one under test, inner and all.
        Exception? caught = null;
        try
        {
            using var payload = await client.GetImageArrayBytesAsync(BaseUrl, "camera", 0, "imagearray", ct);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        output.WriteLine($"{caught?.GetType().Name}: {caught?.Message} / inner {caught?.InnerException?.GetType().Name}");
        caught.ShouldBeOfType<TaskCanceledException>();
        caught.InnerException.ShouldBeOfType<TimeoutException>();
        ct.IsCancellationRequested.ShouldBeFalse();
    }

    // Serves one fixed ImageBytes body for every request, as a server would, with or without its length.
    private sealed class FixedBodyHandler(byte[] body, bool declareLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A seekable stream lets StreamContent declare Content-Length; an unseekable one is a chunked body.
            Stream stream = declareLength ? new MemoryStream(body, writable: false) : new UnseekableStream(body);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(AlpacaImageBytes.MimeType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    // Declares a body and never sends it.
    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new StalledStream());
            content.Headers.ContentType = new MediaTypeHeaderValue(AlpacaImageBytes.MimeType);
            content.Headers.ContentLength = 1_000_000;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    // A body with no length to declare, handed out in odd-sized pieces as a connection does.
    private sealed class UnseekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, 65_521), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A read that completes only when its token is cancelled.
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new ValueTask<int>(new TaskCompletionSource<int>().Task.WaitAsync(cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => new TaskCompletionSource<int>().Task.WaitAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("a stalled body only reads asynchronously");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
