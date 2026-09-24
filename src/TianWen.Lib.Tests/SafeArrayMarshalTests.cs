using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using TianWen.Lib.Devices.Ascom.ComInterop;
using TianWen.Lib.Imaging;
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

    // An ImageArray's (x, y) sits at x + y * width in the SAFEARRAY, which is a row-major height x width
    // frame already: the direct read must land every sample, and the range, exactly where the int copy
    // plus transpose did. 37 x 11 leaves a ragged tail after both the 4-lane int and 8-lane short loops.
    [Theory]
    [InlineData(VT_I4)]
    [InlineData(VT_I2)]
    public void AnImageArrayReadsIntoAChannelExactlyAsTheIntCopyAndTransposeDid(ushort vt)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        var (psa, width, height) = CreateImageArray(vt, 37, 11, (x, y) => unchecked((x * 73856093) ^ (y * 19349663)));
        try
        {
            var expected = Channel.FromWxHImageData(SafeArrayMarshal.ToInt32Array2D(psa));
            var actual = SafeArrayMarshal.ToImageChannel(psa, recycled: null);

            actual.Height.ShouldBe(height);
            actual.Width.ShouldBe(width);
            actual.AsSpan().ToArray().ShouldBe(expected.AsSpan().ToArray());
            (actual.MinValue, actual.MaxValue, actual.Filter, actual.Index)
                .ShouldBe((expected.MinValue, expected.MaxValue, expected.Filter, expected.Index));
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    // FromWxHImageData starts its maximum at zero, so a frame with no positive sample reports a zero
    // maximum. A direct read seeding its maximum from the samples would hand every consumer of MaxValue
    // (the stretch, the unit scale) a different number for the same frame.
    [Fact]
    public void AFrameWithNoPositiveSampleReportsAZeroMaximumAsItAlwaysDid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        var (psa, _, _) = CreateImageArray(VT_I4, 9, 5, (x, y) => -1 - x - y);
        try
        {
            var expected = Channel.FromWxHImageData(SafeArrayMarshal.ToInt32Array2D(psa));
            var actual = SafeArrayMarshal.ToImageChannel(psa, recycled: null);

            expected.MaxValue.ShouldBe(0f);
            actual.MaxValue.ShouldBe(0f);
            actual.MinValue.ShouldBe(expected.MinValue);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void ARecycledPlaneIsReusedOnlyWhenItsShapeMatches()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        var (psa, width, height) = CreateImageArray(VT_I4, 12, 7, (x, y) => x + 100 * y);
        try
        {
            var matching = new float[height, width];
            SafeArrayMarshal.ToImageChannel(psa, matching).Data.ShouldBeSameAs(matching);

            // Transposed, as a ROI or binning change leaves the free list: dropped, and never written.
            var mismatched = new float[width, height];
            SafeArrayMarshal.ToImageChannel(psa, mismatched).Data.ShouldNotBeSameAs(mismatched);
            mismatched.Cast<float>().ShouldAllBe(v => v == 0f);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    // The reason for the direct read: the flat int copy and the reshaped int[,] were 8 bytes a pixel of
    // garbage on every frame (17 MB for a 2 MP guide frame), and with a recycled plane nothing else is
    // left to allocate. The old path is measured beside it, so the bound is read against what it replaced.
    [Fact]
    public void ReadingIntoARecycledPlaneAllocatesNoIntermediateArrays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SAFEARRAY marshaling is Windows-only.");

        var (psa, width, height) = CreateImageArray(VT_I4, 640, 480, (x, y) => x * 7 + y);
        try
        {
            var recycled = new float[height, width];

            // Once each first, so neither measurement carries a JIT or first-call cost.
            _ = Channel.FromWxHImageData(SafeArrayMarshal.ToInt32Array2D(psa), recycled);
            _ = SafeArrayMarshal.ToImageChannel(psa, recycled);

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = Channel.FromWxHImageData(SafeArrayMarshal.ToInt32Array2D(psa), recycled);
            var transposed = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            var channel = SafeArrayMarshal.ToImageChannel(psa, recycled);
            var direct = GC.GetAllocatedBytesForCurrentThread() - before;

            TestContext.Current.TestOutputHelper?.WriteLine($"{width}x{height}: int copy + transpose {transposed:N0} B, direct read {direct:N0} B");
            channel.Data.ShouldBeSameAs(recycled);
            transposed.ShouldBeGreaterThanOrEqualTo(8L * width * height);
            direct.ShouldBeLessThan(1024L);
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

        var empty = SafeArrayMarshal.ToImageChannel(0, recycled: null);
        var expected = Channel.FromWxHImageData(new int[0, 0]);
        empty.Length.ShouldBe(0);
        (empty.MinValue, empty.MaxValue).ShouldBe((expected.MinValue, expected.MaxValue));
    }

    // An ASCOM-shaped [width, height] SAFEARRAY. The dimensions are read back as the reader sees them and
    // the block is filled in its column-major convention (dimension 1 fastest), so nothing here depends on
    // the order SafeArrayCreate takes its bounds in. A VT_I2 array keeps the low 16 bits of each value.
    private static (nint Psa, int Width, int Height) CreateImageArray(ushort vt, int width, int height, Func<int, int, int> pixel)
    {
        SafeArrayBound[] bounds =
        [
            new SafeArrayBound { cElements = (uint)width, lLbound = 0 },
            new SafeArrayBound { cElements = (uint)height, lLbound = 0 },
        ];
        var psa = SafeArrayCreate(vt, 2, bounds);
        psa.ShouldNotBe(0);

        var len1 = Length(psa, 1);
        var len2 = Length(psa, 2);
        Marshal.ThrowExceptionForHR(SafeArrayAccessData(psa, out var data));
        try
        {
            if (vt == VT_I2)
            {
                var flat = new short[len1 * len2];
                for (var j = 0; j < len2; j++)
                {
                    for (var i = 0; i < len1; i++)
                    {
                        flat[i + j * len1] = unchecked((short)pixel(i, j));
                    }
                }
                Marshal.Copy(flat, 0, data, flat.Length);
            }
            else
            {
                var flat = new int[len1 * len2];
                for (var j = 0; j < len2; j++)
                {
                    for (var i = 0; i < len1; i++)
                    {
                        flat[i + j * len1] = pixel(i, j);
                    }
                }
                Marshal.Copy(flat, 0, data, flat.Length);
            }
        }
        finally
        {
            SafeArrayUnaccessData(psa);
        }

        return (psa, len1, len2);
    }

    private static int Length(nint psa, uint dim)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayGetLBound(psa, dim, out var lb));
        Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayGetUBound(psa, dim, out var ub));
        return ub - lb + 1;
    }
}
