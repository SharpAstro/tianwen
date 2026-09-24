using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Widens a sensor's integer samples into a float plane in ONE pass, reporting the range it saw.
/// </summary>
/// <remarks>
/// <para><b>The samples are UNSIGNED.</b> A 16-bit converter (IMX571, IMX455) delivers up to 65535,
/// and Player One left-aligns a 12-bit sensor up to 65520, so a signed read turns every pixel of
/// 32768 or more, which is every bright star core, into a large negative float. The DAL download did
/// exactly that until 2026-09-24 (<c>DALCameraDownloadTests</c>).</para>
/// <para><b>One fused vector pass, read in place.</b> Measured on a 26 MP frame (win-arm64, Release,
/// 2026-09-24): 3.5 ms and no allocation, against 34.5 ms and a 52 MB <c>short[]</c> for the copy,
/// 2-D indexer and per-pixel <c>MathF.Min</c>/<c>Max</c> it replaced; <c>TensorPrimitives</c> in three
/// passes (convert, min, max) took 10.2 ms. The range comes from the integer lanes, before the
/// widening, so it costs one compare per sample and no float compares at all.</para>
/// </remarks>
internal static class RawPixelConversion
{
    /// <summary>Widens 16-bit unsigned samples into <paramref name="destination"/>.</summary>
    /// <returns>The smallest and largest sample, or (0, 0) for an empty source.</returns>
    public static (ushort Min, ushort Max) WidenToSingle(ReadOnlySpan<ushort> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException($"Destination holds {destination.Length} samples, source has {source.Length}", nameof(destination));
        }

        if (source.IsEmpty)
        {
            return (0, 0);
        }

        ushort min = ushort.MaxValue, max = ushort.MinValue;
        var i = 0;
        var lanes = Vector<ushort>.Count;
        if (Vector.IsHardwareAccelerated && source.Length >= lanes)
        {
            var vmin = new Vector<ushort>(ushort.MaxValue);
            var vmax = Vector<ushort>.Zero;
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);
            for (; i <= source.Length - lanes; i += lanes)
            {
                var v = Vector.LoadUnsafe(ref src, (nuint)i);
                vmin = Vector.Min(vmin, v);
                vmax = Vector.Max(vmax, v);
                Vector.Widen(v, out Vector<uint> lo, out Vector<uint> hi);
                Vector.ConvertToSingle(lo).StoreUnsafe(ref dst, (nuint)i);
                Vector.ConvertToSingle(hi).StoreUnsafe(ref dst, (nuint)(i + Vector<uint>.Count));
            }

            for (var k = 0; k < lanes; k++)
            {
                min = Math.Min(min, vmin[k]);
                max = Math.Max(max, vmax[k]);
            }
        }

        for (; i < source.Length; i++)
        {
            var v = source[i];
            destination[i] = v;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        return (min, max);
    }

    /// <summary>Widens 8-bit samples into <paramref name="destination"/>.</summary>
    /// <returns>The smallest and largest sample, or (0, 0) for an empty source.</returns>
    public static (byte Min, byte Max) WidenToSingle(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException($"Destination holds {destination.Length} samples, source has {source.Length}", nameof(destination));
        }

        if (source.IsEmpty)
        {
            return (0, 0);
        }

        byte min = byte.MaxValue, max = byte.MinValue;
        var i = 0;
        var lanes = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && source.Length >= lanes)
        {
            var vmin = new Vector<byte>(byte.MaxValue);
            var vmax = Vector<byte>.Zero;
            var quarter = Vector<uint>.Count;
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);
            for (; i <= source.Length - lanes; i += lanes)
            {
                var v = Vector.LoadUnsafe(ref src, (nuint)i);
                vmin = Vector.Min(vmin, v);
                vmax = Vector.Max(vmax, v);
                Vector.Widen(v, out Vector<ushort> lo, out Vector<ushort> hi);
                Vector.Widen(lo, out Vector<uint> q0, out Vector<uint> q1);
                Vector.Widen(hi, out Vector<uint> q2, out Vector<uint> q3);
                Vector.ConvertToSingle(q0).StoreUnsafe(ref dst, (nuint)i);
                Vector.ConvertToSingle(q1).StoreUnsafe(ref dst, (nuint)(i + quarter));
                Vector.ConvertToSingle(q2).StoreUnsafe(ref dst, (nuint)(i + 2 * quarter));
                Vector.ConvertToSingle(q3).StoreUnsafe(ref dst, (nuint)(i + 3 * quarter));
            }

            for (var k = 0; k < lanes; k++)
            {
                min = Math.Min(min, vmin[k]);
                max = Math.Max(max, vmax[k]);
            }
        }

        for (; i < source.Length; i++)
        {
            var v = source[i];
            destination[i] = v;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        return (min, max);
    }
}
