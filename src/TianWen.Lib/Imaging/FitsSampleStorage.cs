using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// How a plane's samples are LAID DOWN in a FITS file: the container width, and the affine that
/// turns a stored integer back into the value it stands for. FITS defines that affine as
/// <c>physical = BZERO + BSCALE * raw</c>, and this is that pair plus the <see cref="BitDepth"/>
/// they belong to, so the three can never be written out of step with each other.
/// </summary>
/// <remarks>
/// <para><b>It separates the container from the meaning.</b> An <see cref="Image"/> says what its
/// samples ARE; this says how many bits they get on disk and at what step. The two are usually the
/// same choice, which is why a writer without this falls back to the conventional storage for the
/// image's own depth (<see cref="Conventional"/>) and nothing changes.</para>
///
/// <para><b>Why it exists: a per-pixel map does not need 24 bits of mantissa.</b> A coverage plane
/// is a count whose maximum is the frame count, and a rejection plane is a fraction; both were
/// written as float32 because that is what the plane is held in, and both are dominated by
/// mantissa noise that no compressor can do anything with. Measured on one real 3072x3060x3
/// coverage map of 81 frames: 112.8 MB as float32, and gzip takes it only to 94.7 MB (1.2x),
/// because the low mantissa bits of a weight are incompressible. Quantised to 16-bit first it is
/// 56.4 MB and gzips to 2.09 MB, and at 8-bit 28.2 MB gzipping to 1.71 MB. <b>The compression
/// comes from the quantisation, not from the compressor.</b></para>
///
/// <para><b>A quantised write rounds; the conventional one still truncates, deliberately.</b>
/// Rounding is the right conversion and is what a scaled map gets. The conventional storage keeps
/// the cast this writer has always done, because changing it moves every integer-depth write of
/// non-integral data by up to one step: a master dark is the average of its frames and is written
/// at the frames' own depth, and the synthetic fixtures are Int16 over a MAD of 0.5, where one ADU
/// is enough to change which frames a quality gate admits (measured: 8 frames admitted became 5).
/// That is a real change worth making on its own evidence, not a side effect of a storage feature.
/// <see cref="RoundToNearest"/> is the switch.</para>
///
/// <para>Two things do change on every path, both strictly safer than what they replace: a value
/// outside the container CLAMPS where the cast wrapped, and a non-finite sample lands at the bottom
/// of the range (for a map, its "nothing here" value) where the cast produced mid-scale grey.</para>
/// </remarks>
/// <param name="Depth">Container width, written as <c>BITPIX</c>.</param>
/// <param name="BScale">Physical units per stored step, written as <c>BSCALE</c>.</param>
/// <param name="BZero">Physical value of stored zero, written as <c>BZERO</c>.</param>
public readonly record struct FitsSampleStorage(BitDepth Depth, double BScale, double BZero)
{
    /// <summary>Whether a physical value rounds to the nearest stored step (a quantised map) or is
    /// truncated toward zero after the offset (what an unasked write has always done).</summary>
    public bool RoundToNearest { get; init; } = true;

    /// <summary>
    /// The storage a given depth gets when nobody asks for anything else: unit steps, the offset
    /// that makes a signed container hold unsigned values, and the historical truncation. Every
    /// finite in-range sample therefore writes the byte it always wrote; the two exceptions are
    /// stated in the type remarks (clamping, and non-finite samples).
    /// </summary>
    public static FitsSampleStorage Conventional(BitDepth depth) => depth switch
    {
        // BITPIX 8 is unsigned in FITS, so it needs no offset.
        BitDepth.Int8 => new FitsSampleStorage(depth, 1.0, 0.0) { RoundToNearest = false },
        // BITPIX 16 is signed; 32768 is the customary offset that lets it carry [0, 65535].
        BitDepth.Int16 => new FitsSampleStorage(depth, 1.0, 32768.0) { RoundToNearest = false },
        // A label map is never negative and 32-bit signed reaches far enough without an offset.
        BitDepth.Int32 => new FitsSampleStorage(depth, 1.0, 0.0) { RoundToNearest = false },
        _ => new FitsSampleStorage(depth, 1.0, 0.0) { RoundToNearest = false }
    };

    /// <summary>
    /// Spread <c>[0, physicalMax]</c> over the whole of <paramref name="depth"/>'s range, so the
    /// step is the largest the data allow rather than a fixed one. A map whose values are already
    /// whole numbers within range keeps unit steps instead (<see cref="Conventional"/>), because a
    /// count stored as a count reads correctly in any tool without the reader having to believe
    /// the scale.
    /// </summary>
    /// <param name="depth">Container width; must be integral.</param>
    /// <param name="physicalMax">The largest value the plane holds. Zero, negative or non-finite
    /// falls back to unit steps, there being no range to spread.</param>
    /// <param name="valuesAreWholeNumbers">True when every sample is already an integer, so unit
    /// steps are exact and preferable.</param>
    public static FitsSampleStorage Spanning(BitDepth depth, double physicalMax, bool valuesAreWholeNumbers)
    {
        if (!depth.IsIntegral)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Sample storage scaling only applies to an integer container.");
        }

        // Unit steps, but ROUNDING: a map always rounds, whichever branch below answers. Only the
        // conventional path an unasked write takes keeps the historical truncation.
        var unitSteps = Conventional(depth) with { RoundToNearest = true };
        if (!double.IsFinite(physicalMax) || physicalMax <= 0)
        {
            return unitSteps;
        }

        var span = UnsignedSpan(depth);
        if (valuesAreWholeNumbers && physicalMax <= span)
        {
            return unitSteps;
        }

        var bscale = physicalMax / span;
        // The offset is the conventional one measured in the NEW step: a signed container still has
        // to carry the unsigned range, and it does that at whatever step the data chose. Rounding,
        // unlike the conventional path: a quantised value is not an integer waiting to be recovered,
        // so truncating it would bias the whole map down by half a step.
        return new FitsSampleStorage(depth, bscale, unitSteps.BZero * bscale) { RoundToNearest = true };
    }

    /// <summary>How many stored steps the container has above its zero: 255 for a byte, 65535 for a
    /// 16-bit word carrying unsigned values, and the signed ceiling for wider ones.</summary>
    public static double UnsignedSpan(BitDepth depth) => depth switch
    {
        BitDepth.Int8 => byte.MaxValue,
        BitDepth.Int16 => ushort.MaxValue,
        BitDepth.Int32 => int.MaxValue,
        _ => 1.0
    };

    /// <summary>The stored integer for a physical value: rounded to nearest, clamped to the
    /// container, and with a non-finite sample landing on the bottom of the range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long ToRaw(double physical)
    {
        var (lo, hi) = RawRange;
        if (!double.IsFinite(physical))
        {
            return lo;
        }

        var shifted = (physical - BZero) / BScale;
        var raw = RoundToNearest ? Math.Round(shifted, MidpointRounding.AwayFromZero) : Math.Truncate(shifted);
        return raw <= lo ? lo : raw >= hi ? hi : (long)raw;
    }

    /// <summary>
    /// The stored integer for a float sample, which is what every plane actually holds.
    /// </summary>
    /// <remarks>
    /// The conventional storage does the arithmetic in FLOAT, exactly as the cast it replaces did.
    /// This is not pedantry: at a 32768 offset a float subtraction rounds to a multiple of about
    /// 0.0039, so a physical value within that of an integer lands on the integer in float and just
    /// below it in double, and the two then truncate one step apart. With millions of pixels per
    /// frame that case arrives. "Unchanged" has to mean unchanged.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long ToRaw(float physical)
    {
        if (RoundToNearest || BScale != 1.0)
        {
            return ToRaw((double)physical);
        }

        var (lo, hi) = RawRange;
        if (!float.IsFinite(physical))
        {
            return lo;
        }

        var shifted = MathF.Truncate(physical - (float)BZero);
        return shifted <= lo ? lo : shifted >= hi ? hi : (long)shifted;
    }

    /// <summary>
    /// Narrows one ROW of samples into the container, which is how the writer actually calls this.
    /// </summary>
    /// <remarks>
    /// <para><b>Per row rather than per sample, because everything here is loop-invariant.</b> The
    /// container bounds come off a switch, the offset needs a conversion, and the two mode tests are
    /// the same answer for every pixel of every plane; asked per sample they are re-derived tens of
    /// millions of times per map. Hoisted once per row they are a handful of registers, and the
    /// inner loop is a subtract, a truncate and two compares. This is the same span-per-row shape
    /// the rest of the plane code uses, for the same reason.</para>
    /// <para><b>Worth 1.6x, and that is where it stops</b> (<c>FitsNarrowBenchmarks</c>, win-x64,
    /// 3072 square, 9.4M samples): conventional 33.13 ms per sample against 20.31 per row, spanning
    /// 35.61 against 20.93, no allocation either way. Less than the shape suggests, the JIT having
    /// recovered some of it already. <b>The two per-row cases land within 3% of each other although
    /// one truncates a float and the other does a double DIVISION per sample, which says the loop
    /// is not ALU-bound</b> -- so vectorising it would buy little, and it is left scalar.</para>
    /// <para>The scalar <see cref="ToRaw(float)"/> remains the DEFINITION and the two must agree;
    /// it is what a test or a one-off conversion calls, and the branches below are its three cases
    /// lifted out of the loop, not a second rule.</para>
    /// </remarks>
    public readonly void Narrow<T>(ReadOnlySpan<float> source, Span<T> destination)
        where T : struct, INumberBase<T>
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} of {source.Length} samples.", nameof(destination));
        }

        var (lo, hi) = RawRange;
        var loT = T.CreateTruncating(lo);
        var hiT = T.CreateTruncating(hi);

        // The conventional case: unit steps, truncating, and the subtraction done in FLOAT exactly
        // as the cast this replaces did (see ToRaw(float) for why that precision is load-bearing).
        if (!RoundToNearest && BScale == 1.0)
        {
            var bzero = (float)BZero;
            var loF = (float)lo;
            var hiF = (float)hi;
            for (var i = 0; i < source.Length; i++)
            {
                var v = source[i];
                if (!float.IsFinite(v))
                {
                    destination[i] = loT;
                    continue;
                }

                var shifted = MathF.Truncate(v - bzero);
                destination[i] = shifted <= loF ? loT : shifted >= hiF ? hiT : T.CreateTruncating(shifted);
            }

            return;
        }

        // Divided, not multiplied by a reciprocal: the scalar below divides, and "the two agree"
        // has to hold to the last ulp or a test that pins one is not pinning the other. A division
        // per sample is a few tens of ms over a whole map, against the tenths of a second the
        // compression costs.
        var scale = BScale;
        var offset = BZero;
        var rounds = RoundToNearest;
        var loD = (double)lo;
        var hiD = (double)hi;
        for (var i = 0; i < source.Length; i++)
        {
            var v = source[i];
            if (!float.IsFinite(v))
            {
                destination[i] = loT;
                continue;
            }

            var shifted = (v - offset) / scale;
            var raw = rounds ? Math.Round(shifted, MidpointRounding.AwayFromZero) : Math.Truncate(shifted);
            destination[i] = raw <= loD ? loT : raw >= hiD ? hiT : T.CreateTruncating(raw);
        }
    }

    /// <summary>What the container can hold, as stored integers.</summary>
    public readonly (long Low, long High) RawRange
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Depth switch
        {
            BitDepth.Int8 => (byte.MinValue, byte.MaxValue),
            BitDepth.Int16 => (short.MinValue, short.MaxValue),
            BitDepth.Int32 => (int.MinValue, int.MaxValue),
            _ => (0L, 0L)
        };
    }
}
