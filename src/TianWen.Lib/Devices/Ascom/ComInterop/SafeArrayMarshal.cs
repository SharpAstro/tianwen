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
