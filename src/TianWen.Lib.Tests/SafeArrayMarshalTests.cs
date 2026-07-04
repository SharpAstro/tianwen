using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using TianWen.Lib.Devices.Ascom.ComInterop;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Round-trips hand-built SAFEARRAYs through <see cref="SafeArrayMarshal"/>. This is the local
/// (no ASCOM Platform, no COM object) validation of the marshaling that replaced the broken
/// <c>ComVariant.As&lt;T[]&gt;()</c> path -- see docs/plans/ascom-safearray-marshaling.md. Builds the
/// SAFEARRAYs with OLE Automation APIs directly, so it runs on any Windows box (incl. win-arm64).
/// The full end-to-end (a live OmniSim camera SAFEARRAY, incl. 2-D orientation) is covered by the
/// ascom-sim CI leg.
/// </summary>
[SupportedOSPlatform("Windows")]
public class SafeArrayMarshalTests
{
    private const ushort VT_I2 = 2;
    private const ushort VT_I4 = 3;
    private const ushort VT_BSTR = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SafeArrayBound
    {
        public uint cElements;
        public int lLbound;
    }

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreate(ushort vt, uint cDims, [In] SafeArrayBound[] rgsabound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPutElement(nint psa, [In] int[] rgIndices, nint pv);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(nint psa, out nint ppvData);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(nint psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);

    [Fact]
    public void ToInt32Array_RoundTripsVtI4Vector()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        int[] expected = [10, 20, 30, -5, 0];
        var psa = SafeArrayCreateVector(VT_I4, 0, (uint)expected.Length);
        psa.ShouldNotBe(0);
        try
        {
            Marshal.ThrowExceptionForHR(SafeArrayAccessData(psa, out var data));
            Marshal.Copy(expected, 0, data, expected.Length);
            SafeArrayUnaccessData(psa);

            SafeArrayMarshal.ToInt32Array(psa).ShouldBe(expected);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void ToInt32Array_WidensVtI2Vector()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        short[] source = [1, -2, 32767, -32768];
        var psa = SafeArrayCreateVector(VT_I2, 0, (uint)source.Length);
        psa.ShouldNotBe(0);
        try
        {
            Marshal.ThrowExceptionForHR(SafeArrayAccessData(psa, out var data));
            for (var i = 0; i < source.Length; i++)
            {
                Marshal.WriteInt16(data, i * sizeof(short), source[i]);
            }
            SafeArrayUnaccessData(psa);

            SafeArrayMarshal.ToInt32Array(psa).ShouldBe([1, -2, 32767, -32768]);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void ToStringArray_RoundTripsVtBstrVector()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        string[] expected = ["Luminance", "Red", "H-alpha", ""];
        var psa = SafeArrayCreateVector(VT_BSTR, 0, (uint)expected.Length);
        psa.ShouldNotBe(0);
        try
        {
            for (var i = 0; i < expected.Length; i++)
            {
                var bstr = Marshal.StringToBSTR(expected[i]);
                try
                {
                    // For VT_BSTR, SafeArrayPutElement copies the BSTR passed as pv (the pointer itself).
                    Marshal.ThrowExceptionForHR(SafeArrayPutElement(psa, [i], bstr));
                }
                finally
                {
                    Marshal.FreeBSTR(bstr);
                }
            }

            SafeArrayMarshal.ToStringArray(psa).ShouldBe(expected);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void ToInt32Array2D_RoundTripsAndPreservesShape()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        // A non-square 2-D SAFEARRAY so a dimension swap would be caught.
        SafeArrayBound[] bounds =
        [
            new SafeArrayBound { cElements = 3, lLbound = 0 },
            new SafeArrayBound { cElements = 2, lLbound = 0 },
        ];
        var psa = SafeArrayCreate(VT_I4, 2, bounds);
        psa.ShouldNotBe(0);
        try
        {
            // Read back the actual per-dimension lengths the reader will see, then fill the raw block
            // in the reader's column-major convention (dim-1 fastest): flat[i + j*len1]. The assert
            // then proves the reader reconstructs exactly what was written at each logical (i, j).
            var len1 = Length(psa, 1);
            var len2 = Length(psa, 2);
            (len1 * len2).ShouldBe(6);

            var flat = new int[len1 * len2];
            for (var j = 0; j < len2; j++)
            {
                for (var i = 0; i < len1; i++)
                {
                    flat[i + j * len1] = i * 100 + j;
                }
            }

            Marshal.ThrowExceptionForHR(SafeArrayAccessData(psa, out var data));
            Marshal.Copy(flat, 0, data, flat.Length);
            SafeArrayUnaccessData(psa);

            var result = SafeArrayMarshal.ToInt32Array2D(psa);
            result.GetLength(0).ShouldBe(len1);
            result.GetLength(1).ShouldBe(len2);
            for (var i = 0; i < len1; i++)
            {
                for (var j = 0; j < len2; j++)
                {
                    result[i, j].ShouldBe(i * 100 + j);
                }
            }
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void ToInt32Array_NullPointerYieldsEmpty()
    {
        SafeArrayMarshal.ToInt32Array(0).ShouldBeEmpty();
        SafeArrayMarshal.ToStringArray(0).ShouldBeEmpty();
        SafeArrayMarshal.ToInt32Array2D(0).Length.ShouldBe(0);
    }

    private static int Length(nint psa, uint dim)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayGetLBound(psa, dim, out var lb));
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayGetUBound(psa, dim, out var ub));
        return ub - lb + 1;
    }
}
