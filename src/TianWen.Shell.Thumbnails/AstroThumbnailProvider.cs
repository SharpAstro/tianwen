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
    internal sealed unsafe partial class AstroThumbnailProvider : IInitializeWithStream, IThumbnailProvider
    {
        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        private const int HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED = unchecked((int)0x800700B7);
        private const int WTSAT_RGB = 1;

        private byte[]? _bytes;

        public int Initialize(IStream pstream, uint grfMode)
        {
            if (_bytes is not null)
            {
                return HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED;
            }

            try
            {
                // Read the whole file. FITS has no sub-structure a thumbnail could stop early at (the
                // pixels ARE the file), and the stream is marshalled across a process boundary, where a
                // few large reads cost far less than many small ones. 1 MiB chunks: 18 MB in ~20 ms.
                var buffered = new MemoryStream();
                var chunk = new byte[1 << 20];
                fixed (byte* p = chunk)
                {
                    while (true)
                    {
                        uint read = 0;
                        var hr = pstream.Read(p, (uint)chunk.Length, &read);
                        if (hr < 0)
                        {
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

                _bytes = buffered.ToArray();
                return S_OK;
            }
            catch (OutOfMemoryException)
            {
                return E_OUTOFMEMORY;
            }
            catch (Exception ex)
            {
                return ex.HResult != 0 ? ex.HResult : E_FAIL;
            }
        }

        public int GetThumbnail(uint cx, nint* phbmp, int* pdwAlpha)
        {
            *phbmp = 0;
            *pdwAlpha = 0;
            if (_bytes is null)
            {
                return E_UNEXPECTED;
            }

            try
            {
                var maxEdge = (int)Math.Min(cx, int.MaxValue);

                // The one blocking wait in the product, and it is unavoidable: IThumbnailProvider is a
                // synchronous COM vtable call on a surrogate thread with no synchronisation context, so
                // the debayer's continuations run on the pool and this thread simply waits for them. There
                // is nothing to make async here; the caller is the shell.
                var raster = ThumbnailRenderer
                    .RenderAsync(new MemoryStream(_bytes, writable: false), maxEdge)
                    .GetAwaiter()
                    .GetResult();

                var hbmp = Gdi32.CreateTopDownBgra32(raster);
                if (hbmp == 0)
                {
                    return E_FAIL;
                }

                *phbmp = hbmp;
                *pdwAlpha = WTSAT_RGB;
                return S_OK;
            }
            catch (OutOfMemoryException)
            {
                return E_OUTOFMEMORY;
            }
            catch (Exception ex)
            {
                // A file this handler cannot render (a FITS table with no image, a truncated SER) is an
                // HRESULT back to the shell, which then draws the generic file icon. Never an exception
                // across the boundary.
                return ex.HResult != 0 ? ex.HResult : E_FAIL;
            }
        }
    }
}
