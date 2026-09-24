using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

// The read into a frame channel needs TianWen.Lib's imaging types, and tianwen-ascomhost compiles
// SafeArrayMarshal.cs as a linked source with the BCL and COM interop only (TianWen.AscomHost.csproj),
// so it is this part, which only TianWen.Lib compiles.
internal static unsafe partial class SafeArrayMarshal
{
    /// <summary>
    /// An ASCOM camera's 2-D <c>ImageArray</c> SAFEARRAY straight into a frame channel, in ONE pass from the
    /// SAFEARRAY's own memory.
    /// </summary>
    /// <remarks>
    /// <para><b>No transpose is needed.</b> A SAFEARRAY is column-major, dimension 1 fastest, so the
    /// <c>[width, height]</c> array ASCOM returns stores element (x, y) at <c>x + y * width</c>, which IS
    /// a <c>height x width</c> frame in row-major order. <see cref="ToInt32Array2D"/> followed by
    /// <see cref="Imaging.Channel.FromWxHImageData"/> reached the same plane through a flat <c>int</c> copy,
    /// a reshaped <c>int[width, height]</c> and a transpose: 8 bytes a pixel of garbage per frame, 17 MB for
    /// a 2 MP guide frame. Result, min and max are exactly theirs (pinned), including the max starting at 0.</para>
    /// <para>The plane is <paramref name="recycled"/> when its shape matches (a driver's free list), else new.
    /// An empty or non-2-D array gives the same empty channel the old path did.</para>
    /// </remarks>
    public static Imaging.Channel ToImageChannel(nint psa, float[,]? recycled)
    {
        var width = psa == 0 || NativeMethods.SafeArrayGetDim(psa) != 2 ? 0 : GetLength(psa, 1);
        var height = width <= 0 ? 0 : GetLength(psa, 2);
        if (width <= 0 || height <= 0)
        {
            return Imaging.Channel.FromWxHImageData(new int[0, 0], recycled);
        }

        var data = recycled is not null && recycled.GetLength(0) == height && recycled.GetLength(1) == width
            ? recycled
            : new float[height, width];
        var destination = MemoryMarshal.CreateSpan(ref data[0, 0], data.Length);

        float min, max;
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayAccessData(psa, out var raw));
        try
        {
            switch (NativeMethods.SafeArrayGetElemsize(psa))
            {
                case 4:
                    var (imin, imax) = Imaging.RawPixelConversion.WidenToSingle(new ReadOnlySpan<int>((void*)raw, data.Length), destination);
                    (min, max) = (imin, imax);
                    break;
                case 2:
                    var (smin, smax) = Imaging.RawPixelConversion.WidenToSingle(new ReadOnlySpan<short>((void*)raw, data.Length), destination);
                    (min, max) = (smin, smax);
                    break;
                default:
                    // Unknown element sizes yield zeros, as CopyIntegers does for the old path.
                    destination.Clear();
                    (min, max) = (0f, 0f);
                    break;
            }
        }
        finally
        {
            NativeMethods.SafeArrayUnaccessData(psa);
        }

        // FromWxHImageData starts its max at 0, so a frame with no positive sample reports 0: kept.
        return new Imaging.Channel(data, default, min, MathF.Max(0f, max), 0);
    }
}
