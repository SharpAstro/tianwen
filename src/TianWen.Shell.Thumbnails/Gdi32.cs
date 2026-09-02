using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TianWen.Lib.Imaging;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>The one GDI call the provider needs: a 32bpp DIB section the shell's cache can adopt.</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe partial class Gdi32
    {
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        // BITMAPINFO is the header followed by an optional palette; for a 32bpp BI_RGB bitmap there is
        // no palette, so the header alone is a complete BITMAPINFO.
        [LibraryImport("gdi32.dll")]
        private static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* pbmi, uint usage, void** ppvBits, nint hSection, uint offset);

        /// <summary>
        /// Copies an RGBA raster into a new top-down (negative height) 32bpp BGRA DIB section, the layout
        /// the shell's thumbnail cache expects. Returns 0 when GDI refuses. The caller owns the handle;
        /// here that caller is the shell.
        /// </summary>
        internal static nint CreateTopDownBgra32(in ThumbnailRaster raster)
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = raster.Width,
                biHeight = -raster.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            void* bits = null;
            var hbmp = CreateDIBSection(0, &bmi, DIB_RGB_COLORS, &bits, 0, 0);
            if (hbmp == 0 || bits == null)
            {
                return 0;
            }

            var dst = new Span<byte>(bits, raster.Width * raster.Height * 4);
            var src = raster.Rgba.AsSpan(0, dst.Length);
            for (var i = 0; i < dst.Length; i += 4)
            {
                dst[i] = src[i + 2];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i];
                dst[i + 3] = src[i + 3];
            }

            return hbmp;
        }
    }
}
