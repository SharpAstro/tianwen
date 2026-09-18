using System;
using System.Runtime.Versioning;
using System.Threading;
using Shouldly;
using TianWen.Shell.Thumbnails;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The shell handler's POLICY, which is about what it refuses to do rather than about pictures: the
/// imaging itself is <see cref="ThumbnailRendererTests"/>.
/// <para>
/// The invariant worth a test is that READING is deferred and refusable, because a read is what
/// hydrates a cloud placeholder. A handler that buffers the file in <c>Initialize</c> downloads every
/// frame in a folder merely because the shell asked whether a thumbnail exists, and nothing about the
/// resulting picture would look wrong. So these count reads on the stream rather than inspecting the
/// bitmap.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public unsafe class AstroThumbnailProviderTests
{
    private const int S_OK = 0;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private const int HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED = unchecked((int)0x800700B7);

    /// <summary>thumbcache.h, <c>MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0xB203)</c>.</summary>
    private const int WTS_E_FASTEXTRACTIONNOTSUPPORTED = unchecked((int)0x8004B203);

    private const uint WTSCF_DEFAULT = 0;
    private const uint WTSCF_SQUARE = 0x2;
    private const uint WTSCF_FAST = 0x8;

    [Fact]
    public void InitializeTakesTheStreamWithoutReadingAByteOfIt()
    {
        var stream = new CountingStream(Payload(4 << 20));
        var provider = NewProvider();

        provider.Initialize(stream, 0).ShouldBe(S_OK);

        // The whole point: the shell may call Initialize and then never ask for a bitmap, and over a
        // placeholder the first read is the download.
        stream.Reads.ShouldBe(0);
        stream.BytesRead.ShouldBe(0);
    }

    [Fact]
    public void AFastExtractionIsDeclinedWithoutTouchingTheFile()
    {
        var stream = new CountingStream(Payload(4 << 20));
        var provider = NewProvider();
        provider.Initialize(stream, 0).ShouldBe(S_OK);
        provider.SetContext(WTSCF_FAST).ShouldBe(S_OK);

        nint hbmp = 1;
        var alpha = 1;
        var hr = provider.GetThumbnail(256, &hbmp, &alpha);

        hr.ShouldBe(WTS_E_FASTEXTRACTIONNOTSUPPORTED);
        stream.Reads.ShouldBe(0);
        hbmp.ShouldBe(0);
    }

    [Fact]
    public void TheDefaultContextStillReadsTheFile()
    {
        var payload = Payload(3 << 20);
        var stream = new CountingStream(payload);
        var provider = NewProvider();
        provider.Initialize(stream, 0).ShouldBe(S_OK);
        provider.SetContext(WTSCF_DEFAULT).ShouldBe(S_OK);

        nint hbmp = 0;
        var alpha = 0;
        // The bytes are not an image, so this fails; that it failed AFTER reading them is the point,
        // and is what separates the guard above from a handler that has simply stopped working.
        _ = provider.GetThumbnail(256, &hbmp, &alpha);

        stream.BytesRead.ShouldBe(payload.Length);
    }

    [Fact]
    public void AContextWithoutTheFastBitIsNotADeclineOne()
    {
        var payload = Payload(1 << 20);
        var stream = new CountingStream(payload);
        var provider = NewProvider();
        provider.Initialize(stream, 0).ShouldBe(S_OK);
        // Testing the BIT, not the word: a guard written as "any context set" would refuse this, and
        // Explorer's own square-cropped requests would silently stop producing thumbnails.
        provider.SetContext(WTSCF_SQUARE).ShouldBe(S_OK);

        nint hbmp = 0;
        var alpha = 0;
        var hr = provider.GetThumbnail(256, &hbmp, &alpha);

        hr.ShouldNotBe(WTS_E_FASTEXTRACTIONNOTSUPPORTED);
        stream.BytesRead.ShouldBe(payload.Length);
    }

    [Fact]
    public void AskingForTheBitmapBeforeInitializeIsUnexpected()
    {
        var provider = NewProvider();

        nint hbmp = 1;
        var alpha = 1;
        provider.GetThumbnail(256, &hbmp, &alpha).ShouldBe(E_UNEXPECTED);
        hbmp.ShouldBe(0);
    }

    [Fact]
    public void ASecondInitializeIsRefused()
    {
        var provider = NewProvider();
        provider.Initialize(new CountingStream(Payload(16)), 0).ShouldBe(S_OK);

        provider.Initialize(new CountingStream(Payload(16)), 0)
            .ShouldBe(HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED);
    }

    [Fact]
    public void AStreamThatSaysHowBigItIsIsReadInOneGo()
    {
        // The read used to grow a MemoryStream by doubling and then copy it out, so a 150 MB frame put
        // up to three times that on the large object heap for a buffer nothing keeps. Nothing about
        // the picture shows it, and in the surrogate nothing ever collected it (issue #294).
        var payload = Payload(5 << 20);
        var stream = new CountingStream(payload, answersStat: true);
        var provider = NewProvider();
        provider.Initialize(stream, 0).ShouldBe(S_OK);

        nint hbmp = 0;
        var alpha = 0;
        _ = provider.GetThumbnail(256, &hbmp, &alpha);

        stream.Stats.ShouldBe(1);
        stream.Reads.ShouldBe(1, "an exactly sized buffer needs one read, not one per chunk");
        stream.BytesRead.ShouldBe(payload.Length);
    }

    [Fact]
    public void AStreamThatWillNotSayHowBigItIsIsStillReadInFull()
    {
        // Stat is allowed to refuse, and the growable read is why that is not a handler that has
        // stopped working.
        var payload = Payload(3 << 20);
        var stream = new CountingStream(payload, answersStat: false);
        var provider = NewProvider();
        provider.Initialize(stream, 0).ShouldBe(S_OK);

        nint hbmp = 0;
        var alpha = 0;
        _ = provider.GetThumbnail(256, &hbmp, &alpha);

        stream.BytesRead.ShouldBe(payload.Length);
    }

    [Fact]
    public void TheStreamIsLetGoAsSoonAsItsBytesAreRead()
    {
        // Observable here as the second ask being refused rather than quietly re-reading a spent
        // stream. In the surrogate it is the native reference: held until this object is finalized,
        // it would be held for the life of a process that never collects.
        var provider = NewProvider();
        provider.Initialize(new CountingStream(Payload(1 << 20), answersStat: true), 0).ShouldBe(S_OK);

        nint hbmp = 0;
        var alpha = 0;
        _ = provider.GetThumbnail(256, &hbmp, &alpha);

        provider.GetThumbnail(256, &hbmp, &alpha).ShouldBe(E_UNEXPECTED);
        hbmp.ShouldBe(0);
    }

    [Fact]
    public void ARequestThatReadTheFileArmsTheHeapCollapse_AndADeclinedOneDoesNot()
    {
        // The pairing is the point: a WTSCF_FAST decline returns without allocating anything, so
        // waking the timer for it would be a blocking compacting collection bought with nothing to
        // give back.
        using var idle = new IdleHeapCollapse(() => { }, Timeout.Infinite);

        var declined = new AstroThumbnailProvider(idle);
        declined.Initialize(new CountingStream(Payload(1 << 20)), 0).ShouldBe(S_OK);
        declined.SetContext(WTSCF_FAST).ShouldBe(S_OK);
        nint hbmp = 0;
        var alpha = 0;
        _ = declined.GetThumbnail(256, &hbmp, &alpha);
        idle.Renders.ShouldBe(0);

        var read = new AstroThumbnailProvider(idle);
        read.Initialize(new CountingStream(Payload(1 << 20)), 0).ShouldBe(S_OK);
        // These bytes are not an image, so the render fails -- and it still allocated everything up to
        // the decode, which is exactly why the counter is bumped whatever the outcome.
        _ = read.GetThumbnail(256, &hbmp, &alpha);
        idle.Renders.ShouldBe(1);
    }

    /// <summary>
    /// The handler as the shell builds it, minus the process-wide heap collapse: a unit test must not
    /// arm a three-second timer that then runs a blocking compacting collection under the rest of the
    /// suite.
    /// </summary>
    private static AstroThumbnailProvider NewProvider()
        => new AstroThumbnailProvider(new IdleHeapCollapse(() => { }, Timeout.Infinite));

    /// <summary>Not an image, deliberately: no test here is about decoding one.</summary>
    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    /// <summary>
    /// The shell's side of the boundary, reduced to the one method the handler is allowed to call, and
    /// counting every call so a test can assert that none happened.
    /// </summary>
    private sealed unsafe class CountingStream : IStream
    {
        private const int S_FALSE = 1;
        private const int E_NOTIMPL = unchecked((int)0x80004001);

        private readonly byte[] _bytes;
        private readonly bool _answersStat;
        private int _position;

        /// <param name="answersStat">Whether this stream will say how big it is. Both answers are
        /// real: the shell's own file stream does, and a stream is entitled to refuse.</param>
        public CountingStream(byte[] bytes, bool answersStat = false)
        {
            _bytes = bytes;
            _answersStat = answersStat;
        }

        public int Stats { get; private set; }

        public int Reads { get; private set; }

        public long BytesRead { get; private set; }

        public int Read(byte* pv, uint cb, uint* pcbRead)
        {
            Reads++;

            var take = (int)Math.Min(cb, (uint)(_bytes.Length - _position));
            _bytes.AsSpan(_position, take).CopyTo(new Span<byte>(pv, take));
            _position += take;
            BytesRead += take;

            if (pcbRead is not null)
            {
                *pcbRead = (uint)take;
            }

            return take == 0 ? S_FALSE : S_OK;
        }

        public int Write(byte* pv, uint cb, uint* pcbWritten) => E_NOTIMPL;

        public int Seek(long dlibMove, uint dwOrigin, ulong* plibNewPosition) => E_NOTIMPL;

        public int SetSize(ulong libNewSize) => E_NOTIMPL;

        public int CopyTo(nint pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten) => E_NOTIMPL;

        public int Commit(uint grfCommitFlags) => E_NOTIMPL;

        public int Revert() => E_NOTIMPL;

        public int LockRegion(ulong libOffset, ulong cb, uint dwLockType) => E_NOTIMPL;

        public int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) => E_NOTIMPL;

        public int Stat(void* pstatstg, uint grfStatFlag)
        {
            Stats++;
            if (!_answersStat)
            {
                return E_NOTIMPL;
            }

            // Write the whole 80-byte STATSTG the caller declared, not only the field being read: a
            // fake that touches less than the real callee would hide a short declaration on the
            // handler's side, which is the bug that corrupts its stack.
            new Span<byte>(pstatstg, 80).Clear();
            *(ulong*)((byte*)pstatstg + 16) = (ulong)_bytes.Length;
            return S_OK;
        }

        public int Clone(nint* ppstm) => E_NOTIMPL;
    }
}
