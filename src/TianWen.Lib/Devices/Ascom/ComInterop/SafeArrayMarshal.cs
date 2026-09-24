using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Manual SAFEARRAY -> managed array marshaling for ASCOM array-typed COM properties.
/// <para>
/// <see cref="System.Runtime.InteropServices.Marshalling.ComVariant"/>'s <c>As&lt;T&gt;()</c> throws
/// <c>ArgumentException: "Unsupported type"</c> for array <c>T</c> (it does not handle SAFEARRAYs), so
/// <see cref="DispatchObject"/>'s array getters pull the <c>parray</c> out of the VARIANT and copy it
/// out through these helpers instead. Root cause + history: docs/plans/ascom-safearray-marshaling.md.
/// </para>
/// <para>
/// Every method COPIES the data into a managed array while the SAFEARRAY is accessed, so the caller
/// must keep the owning VARIANT alive (not yet <c>VariantClear</c>'d / disposed) across the call.
/// Takes the raw <c>SAFEARRAY*</c> so it is unit-testable against a hand-built SAFEARRAY with no COM
/// object in play.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class SafeArrayMarshal
{
    private const ushort VT_BSTR = 8;

    /// <summary>Marshals a 1-D integer SAFEARRAY (VT_I2 or VT_I4) to <see cref="int"/>[].</summary>
    public static int[] ToInt32Array(nint psa)
    {
        if (psa == 0 || NativeMethods.SafeArrayGetDim(psa) != 1)
        {
            return [];
        }

        var count = GetLength(psa, 1);
        if (count <= 0)
        {
            return [];
        }

        var result = new int[count];
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayAccessData(psa, out var data));
        try
        {
            CopyIntegers(data, NativeMethods.SafeArrayGetElemsize(psa), result);
        }
        finally
        {
            NativeMethods.SafeArrayUnaccessData(psa);
        }
        return result;
    }

    /// <summary>Marshals a 2-D integer SAFEARRAY (VT_I2 or VT_I4) to <see cref="int"/>[,], preserving
    /// the native <c>[dim1, dim2]</c> shape. For an ASCOM camera that is <c>[width(X), height(Y)]</c>;
    /// <see cref="Imaging.Channel.FromWxHImageData"/> transposes W x H -> H x W downstream.</summary>
    public static int[,] ToInt32Array2D(nint psa)
    {
        if (psa == 0 || NativeMethods.SafeArrayGetDim(psa) != 2)
        {
            return new int[0, 0];
        }

        var len1 = GetLength(psa, 1);
        var len2 = GetLength(psa, 2);
        if (len1 <= 0 || len2 <= 0)
        {
            return new int[0, 0];
        }

        var flat = new int[len1 * len2];
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayAccessData(psa, out var data));
        try
        {
            CopyIntegers(data, NativeMethods.SafeArrayGetElemsize(psa), flat);
        }
        finally
        {
            NativeMethods.SafeArrayUnaccessData(psa);
        }

        // SAFEARRAYs are column-major: dimension 1 varies fastest, so logical element (i, j) is at
        // flat index i + j*len1.
        var result = new int[len1, len2];
        for (var j = 0; j < len2; j++)
        {
            var col = j * len1;
            for (var i = 0; i < len1; i++)
            {
                result[i, j] = flat[col + i];
            }
        }
        return result;
    }

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

    /// <summary>Marshals a 1-D BSTR SAFEARRAY (VT_BSTR) to <see cref="string"/>[].</summary>
    public static string[] ToStringArray(nint psa)
    {
        if (psa == 0 || NativeMethods.SafeArrayGetDim(psa) != 1)
        {
            return [];
        }

        if (NativeMethods.SafeArrayGetVartype(psa, out var vt) < 0 || vt != VT_BSTR)
        {
            return [];
        }

        var count = GetLength(psa, 1);
        if (count <= 0)
        {
            return [];
        }

        var result = new string[count];
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayAccessData(psa, out var data));
        try
        {
            // The data block is an array of BSTR pointers.
            var ptrs = new ReadOnlySpan<nint>((void*)data, count);
            for (var i = 0; i < count; i++)
            {
                result[i] = ptrs[i] != 0 ? Marshal.PtrToStringBSTR(ptrs[i]) : string.Empty;
            }
        }
        finally
        {
            NativeMethods.SafeArrayUnaccessData(psa);
        }
        return result;
    }

    /// <summary>Copies <paramref name="dest"/>.Length integer elements from the accessed SAFEARRAY
    /// data, widening 2-byte (VT_I2) elements. Unknown element sizes yield zeros.</summary>
    private static void CopyIntegers(nint data, uint elemSize, int[] dest)
    {
        switch (elemSize)
        {
            case 4:
                new ReadOnlySpan<int>((void*)data, dest.Length).CopyTo(dest);
                break;
            case 2:
                var shorts = new ReadOnlySpan<short>((void*)data, dest.Length);
                for (var i = 0; i < dest.Length; i++)
                {
                    dest[i] = shorts[i];
                }
                break;
        }
    }

    private static int GetLength(nint psa, uint dim)
    {
        if (NativeMethods.SafeArrayGetLBound(psa, dim, out var lb) < 0
            || NativeMethods.SafeArrayGetUBound(psa, dim, out var ub) < 0)
        {
            return 0;
        }
        return ub - lb + 1;
    }
}
