using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TianWen.Lib.Imaging;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// The shell object: initialised once with the file's stream, asked once for a bitmap, then released.
    /// One instance per request, no state between requests. Runs inside the shell's COM surrogate
    /// (dllhost.exe), never in explorer.exe.
    /// <para>
    /// All the imaging is <see cref="ThumbnailRenderer"/> in TianWen.Lib; this class only moves bytes
    /// across the COM boundary in both directions: an <c>IStream</c> in, an <c>HBITMAP</c> out.
    /// </para>
    /// </summary>
    [GeneratedComClass]
    [SupportedOSPlatform("windows")]
    internal sealed unsafe partial class AstroThumbnailProvider : IInitializeWithStream, IThumbnailSettings, IThumbnailProvider
    {
        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        private const int HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED = unchecked((int)0x800700B7);
        private const int WTSAT_RGB = 1;

        /// <summary>
        /// <c>WTS_E_FASTEXTRACTIONNOTSUPPORTED</c>, thumbcache.h: <c>MAKE_HRESULT(SEVERITY_ERROR,
        /// FACILITY_ITF, 0xB203)</c>. The caller asked for a fast extraction and this handler has none
        /// to give, so the shell draws the file-type icon instead.
        /// </summary>
        private const int WTS_E_FASTEXTRACTIONNOTSUPPORTED = unchecked((int)0x8004B203);

        /// <summary><c>WTSCF_FAST</c>, the one <c>WTS_CONTEXTFLAGS</c> bit this handler acts on.</summary>
        private const uint WTSCF_FAST = 0x8;

        /// <summary>
        /// <c>STATFLAG_NONAME</c>: answer without <c>pwcsName</c>. Without it the callee allocates the
        /// name with <c>CoTaskMemAlloc</c> and the caller owes a <c>CoTaskMemFree</c>; asking for no
        /// name means there is nothing to free, and the name is not wanted.
        /// </summary>
        private const uint STATFLAG_NONAME = 1;

        private readonly IdleHeapCollapse _collapse;

        private IStream? _stream;
        private uint _contextFlags;

        /// <summary>The shell's entry point: one instance per request, from the class factory.</summary>
        public AstroThumbnailProvider()
            : this(IdleHeapCollapse.Shared)
        {
        }

        internal AstroThumbnailProvider(IdleHeapCollapse collapse) => _collapse = collapse;

        /// <summary>
        /// Takes the stream and deliberately READS NOTHING, because reading is the expensive and
        /// irreversible half.
        /// <para>
        /// Over a cloud placeholder the first read is what triggers hydration -- the shell asking
        /// whether a file has a thumbnail would silently download it -- and the shell may never call
        /// <see cref="GetThumbnail"/> at all. <see cref="SetContext"/>'s ordering against this method
        /// is unspecified, so the decision has to be taken where both the context flags and the
        /// requested size are known, which is <see cref="GetThumbnail"/> and not here.
        /// </para>
        /// <para>
        /// Holding the <c>IStream</c> across the two calls is the documented shape (the SDK's own
        /// recipe-thumbnail sample does the same); the shell keeps this object alive between them.
        /// </para>
        /// </summary>
        public int Initialize(IStream pstream, uint grfMode)
        {
            if (_stream is not null)
            {
                return HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED;
            }

            _stream = pstream;
            return S_OK;
        }

        /// <summary>
        /// Records the caller's context. Optional for the shell to call, so <see cref="_contextFlags"/>
        /// stays <c>WTSCF_DEFAULT</c> (0) when it does not, which is the permissive answer and matches
        /// the behaviour before this interface existed.
        /// </summary>
        public int SetContext(uint dwContext)
        {
            _contextFlags = dwContext;
            return S_OK;
        }

        /// <summary>
        /// Reads the whole file. FITS has no sub-structure a thumbnail could stop early at (the pixels
        /// ARE the file), and the stream is marshalled across a process boundary, where a few large
        /// reads cost far less than many small ones.
        /// <para>
        /// The size comes from <c>IStream::Stat</c> so the buffer is allocated ONCE at the right size.
        /// The growable read below is what this replaced, and it is kept only as the fallback for a
        /// stream that will not answer: a <see cref="MemoryStream"/> doubles its capacity as it fills
        /// and <c>ToArray</c> then copies the result, so up to three times the file size is live on the
        /// large object heap at once, for a buffer nothing keeps and (issue #294) nothing collects.
        /// </para>
        /// </summary>
        private static int TryBuffer(IStream stream, out byte[] bytes)
        {
            var length = TryStatLength(stream);
            return length > 0 ? ReadExact(stream, (int)length, out bytes) : ReadGrowable(stream, out bytes);
        }

        /// <summary>
        /// The file's size, or 0 for a stream that cannot or will not say. Not recorded as a failure:
        /// <c>Stat</c> is optional in practice and the growable read covers every answer it declines.
        /// </summary>
        private static long TryStatLength(IStream stream)
        {
            STATSTG stat = default;
            if (stream.Stat(&stat, STATFLAG_NONAME) < 0)
            {
                return 0;
            }

            return stat.cbSize > 0 && stat.cbSize <= (ulong)Array.MaxLength ? (long)stat.cbSize : 0;
        }

        /// <summary>
        /// One allocation of exactly the stated size, filled by as few reads as the stream will give.
        /// A stream that hands over fewer bytes than it declared is the stream's business and not a
        /// failure, so the short buffer is trimmed and passed on: the decoder is what judges whether
        /// the bytes are a file.
        /// </summary>
        private static int ReadExact(IStream stream, int length, out byte[] bytes)
        {
            var buffer = new byte[length];
            var offset = 0;

            fixed (byte* p = buffer)
            {
                while (offset < length)
                {
                    uint read = 0;
                    var hr = stream.Read(p + offset, (uint)(length - offset), &read);
                    if (hr < 0)
                    {
                        // The one step where a cloud-backed file is expected to differ from a local
                        // one, so it records how far it got: a refusal at offset zero is an access
                        // decision, one part way through is the stream dying mid-hydration.
                        ThumbnailDiagnostics.Failure("IStream.Read", hr, $"after {offset} of {length} bytes");
                        bytes = Array.Empty<byte>();
                        return hr;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    offset += (int)read;
                    if (hr == S_FALSE)
                    {
                        break;
                    }
                }
            }

            bytes = offset == length ? buffer : buffer[..offset];
            return S_OK;
        }

        /// <summary>
        /// The fallback for a stream whose size is unknown, in 1 MiB chunks: 18 MB in ~20 ms.
        /// </summary>
        private static int ReadGrowable(IStream stream, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();

            var buffered = new MemoryStream();
            var chunk = new byte[1 << 20];
            fixed (byte* p = chunk)
            {
                while (true)
                {
                    uint read = 0;
                    var hr = stream.Read(p, (uint)chunk.Length, &read);
                    if (hr < 0)
                    {
                        // The one step where a cloud-backed file is expected to differ from a local one,
                        // so it records how far it got: a refusal at offset zero is an access decision,
                        // one part way through is the stream dying mid-hydration.
                        ThumbnailDiagnostics.Failure("IStream.Read", hr, $"after {buffered.Length} bytes");
                        return hr;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    buffered.Write(chunk, 0, (int)read);
                    if (hr == S_FALSE)
                    {
                        break;
                    }
                }
            }

            bytes = buffered.ToArray();
            return S_OK;
        }

        public int GetThumbnail(uint cx, nint* phbmp, int* pdwAlpha)
        {
            *phbmp = 0;
            *pdwAlpha = 0;
            if (_stream is not { } stream)
            {
                // The shell asked for the bitmap without a successful Initialize, which it should not do.
                ThumbnailDiagnostics.Failure("GetThumbnail", E_UNEXPECTED, "called before Initialize succeeded");
                return E_UNEXPECTED;
            }

            if ((_contextFlags & WTSCF_FAST) != 0)
            {
                // Declining IS the correct answer here, not a failure to work around. WTSCF_FAST means
                // "answer from something embedded, or do not answer": reading the stream is what pulls a
                // cloud file down, and FITS has no embedded thumbnail to offer instead. Without this the
                // shell would hydrate a whole folder of frames to draw icons.
                ThumbnailDiagnostics.Failure("GetThumbnail", WTS_E_FASTEXTRACTIONNOTSUPPORTED,
                    $"WTSCF_FAST at {cx}px; declined rather than read the file");
                return WTS_E_FASTEXTRACTIONNOTSUPPORTED;
            }

            byte[]? bytes = null;
            try
            {
                var bufferHr = TryBuffer(stream, out bytes);

                // Hand the native stream back the instant its bytes are copied out, rather than at
                // this object's finalization -- which needs a collection, and an idle surrogate never
                // runs one, so the shell's end of a cloud placeholder was being held open for as long
                // as the process lived.
                ReleaseStream();

                if (bufferHr < 0)
                {
                    return bufferHr;
                }

                var maxEdge = (int)Math.Min(cx, int.MaxValue);

                // The one blocking wait in the product, and it is unavoidable: IThumbnailProvider is a
                // synchronous COM vtable call on a surrogate thread with no synchronisation context, so
                // the debayer's continuations run on the pool and this thread simply waits for them. There
                // is nothing to make async here; the caller is the shell.
                var raster = ThumbnailRenderer
                    .RenderAsync(new MemoryStream(bytes, writable: false), maxEdge)
                    .GetAwaiter()
                    .GetResult();

                var hbmp = Gdi32.CreateTopDownBgra32(raster);
                if (hbmp == 0)
                {
                    ThumbnailDiagnostics.Failure("CreateTopDownBgra32", E_FAIL,
                        $"{raster.Width}x{raster.Height} raster rendered but no HBITMAP");
                    return E_FAIL;
                }

                *phbmp = hbmp;
                *pdwAlpha = WTSAT_RGB;
                return S_OK;
            }
            catch (OutOfMemoryException)
            {
                ThumbnailDiagnostics.Failure("GetThumbnail", E_OUTOFMEMORY,
                    $"out of memory over {bytes?.Length ?? 0} bytes at {cx}px");
                return E_OUTOFMEMORY;
            }
            catch (Exception ex)
            {
                // A file this handler cannot render (a FITS table with no image, a truncated SER) is an
                // HRESULT back to the shell, which then draws the generic file icon. Never an exception
                // across the boundary. It is still recorded, because "this file has no image in it" and
                // "the decoder broke" are the same generic icon on screen and different bugs.
                var hr = ex.HResult != 0 ? ex.HResult : E_FAIL;
                ThumbnailDiagnostics.Failure("GetThumbnail", hr, $"{ex.GetType().Name}: {ex.Message}");
                return hr;
            }
            finally
            {
                // Whatever the outcome, this request has put several hundred MB of full-resolution
                // garbage on the heap and nothing else in this process will ever allocate enough to
                // collect it. Recording it here and not in the WTSCF_FAST branch above is deliberate:
                // that one returns without reading a byte, so it has nothing to give back.
                _collapse.RecordRender();
            }
        }

        /// <summary>
        /// Drops the stream and releases the native reference behind it. The wrapper is a
        /// <see cref="ComObject"/> only when the shell handed the stream across a real COM boundary;
        /// in-process callers pass a managed implementation and there is nothing to release.
        /// </summary>
        private void ReleaseStream()
        {
            var stream = _stream;
            _stream = null;

            // Through object: ComObject is sealed and does not implement IStream, so the compiler
            // refuses the pattern on the interface even though the runtime cast succeeds -- a COM
            // wrapper answers for the interface through IDynamicInterfaceCastable, not by declaring it.
            if ((object?)stream is ComObject com)
            {
                com.FinalRelease();
            }
        }

        /// <summary>
        /// <c>STATSTG</c> (objidl.h), declared IN FULL. Only <see cref="cbSize"/> is read, but the
        /// callee writes every field, so a trimmed declaration would have <c>IStream::Stat</c> write
        /// past the end of the caller's struct -- a stack corruption that would look like anything but
        /// its cause. <c>FILETIME</c> is two <c>DWORD</c>s; a <see cref="long"/> gives the same 80-byte
        /// layout and the same offsets on both x64 and arm64.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct STATSTG
        {
            public nint pwcsName;
            public uint type;
            public ulong cbSize;
            public long mtime;
            public long ctime;
            public long atime;
            public uint grfMode;
            public uint grfLocksSupported;
            public Guid clsid;
            public uint grfStateBits;
            public uint reserved;
        }
    }
}
