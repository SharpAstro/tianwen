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

        private IStream? _stream;
        private uint _contextFlags;

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
        /// reads cost far less than many small ones. 1 MiB chunks: 18 MB in ~20 ms.
        /// </summary>
        private static int TryBuffer(IStream stream, out byte[] bytes)
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
        }
    }
}
