using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Multi-channel image: an immutable view over per-channel <see cref="Channel"/>s (each carrying
/// its own plane, filter, min/max, and optional ref-counted camera buffer) plus <see cref="ImageMeta"/>.
/// The image-wide <see cref="MaxValue"/>/<see cref="MinValue"/> are derived across the channels;
/// the raw-array constructor overload wraps legacy <c>float[][,]</c> call sites.
/// </summary>
/// <remarks>
/// <para><b>Frame ownership: own, borrow, consume.</b> This block is the one place the rules are
/// written down, and every producer of an <see cref="Image"/> points back here. The MECHANISM has
/// existed and worked for a while (<see cref="ChannelBuffer"/> refcounts, <see cref="Release"/>,
/// <see cref="TryLease"/>, <c>Array2DPool</c>); what was missing was a stated rule saying which part
/// of it applies to a given instance, so the answer was reconstructed per call site from that call
/// site's own knowledge. See <c>docs/plans/frame-lifecycle.md</c>.</para>
///
/// <para><b>Own</b> -- the frame was handed to you, so you <see cref="Release"/> it exactly once and
/// never touch it afterwards. <b>Borrow</b> -- you did not receive ownership, so take
/// <see cref="TryLease"/> if you need the pixels beyond the current frame, and release the LEASE,
/// never the original. <b>Consume</b> -- you hand your frame to a method that writes through its
/// arrays and returns a view of them; the input is spent, and only the result may be used.</para>
///
/// <para><b>Four conventions coexist and an <see cref="Image"/> carries no runtime indication of
/// which one it is</b>, which is precisely why the producer's documentation is the only place the
/// answer lives. All four are real distinctions between real situations and are deliberately kept;
/// the fifth was not a convention and has been retired (see below):</para>
/// <list type="number">
/// <item><b>Driver-owned, recycled</b> (<c>ICameraDriver.GetImageAsync</c>) -- OWN it.
/// <see cref="Release"/> hands the array back to the camera, so never read it afterwards and never
/// hold it across an <c>await</c> without <see cref="TryLease"/>.</item>
/// <item><b>Self-owned</b> (file loads by default, debayer output, synthetic frames, tests) -- nothing
/// is required of the consumer. <see cref="Release"/> is a no-op and must STAY one: several call
/// sites release an image and go on reading it, which is well defined only under this convention.</item>
/// <item><b>Pool-owned</b> (<c>TryReadFitsFile(..., pooled: true)</c>) -- OWN it, exactly as for a
/// camera frame; here <see cref="Release"/> returns the array to <c>Array2DPool</c> instead.</item>
/// <item><b>Consumed input</b> (<see cref="ScaleFloatValuesToUnitInPlace"/>,
/// <c>AstroImageDocument.AdoptImageAsync</c>, <c>Calibrator.Apply</c>) -- CONSUME: the input is spent
/// and only the result may be used. Some consumers hand back a view of the input's own arrays with
/// <c>Buffer = null</c> so release responsibility stays put; <c>Calibrator.Apply</c> instead takes
/// the ownership over and hands the result's back to the caller. This is the only convention that
/// MUTATES, and the only one with no runtime enforcement whatsoever: conventions 1 and 3 fail loudly
/// (a released <see cref="ChannelBuffer"/> throws), while a second reader of a consumed input fails
/// silently, in the pixels. <b>Membership is a property of the METHOD, never of an argument</b> --
/// <see cref="DebayerAsync"/> used to consume only for some sensor types and only with
/// <c>normalizeToUnit</c> set, which P4 removed.</item>
/// </list>
///
/// <para><b>Conventions 1 and 3 fail loudly, and that took a fix rather than a claim.</b> Reading a
/// frame whose arrays went back to a camera or the pool throws <see cref="ObjectDisposedException"/>
/// from the <c>Planes</c> accessor every read funnels through. It did NOT before:
/// <see cref="ChannelBuffer.Data"/> had guarded itself since it was written and had never had one
/// call site, because pixels are read through <see cref="Channel.Data"/>, a plain field -- so a
/// released camera frame returned whatever the driver had since put in that array. Convention 2 is
/// unaffected: release is a no-op there, "release and keep reading" stays correct, and the guard
/// keys on whether arrays were actually handed back rather than on release having happened.</para>
/// <list type="bullet">
/// <item><b>Convention 4 still fails silently</b>, in the pixels, and cannot be guarded the same way:
/// the hazard is a write THROUGH the array, not a swap of it, so there is no accessor to put a check
/// on. A consumed input is a discipline, not an enforced rule.</item>
/// </list>
///
/// <para><b>There was a fifth -- "identity or copy, decided at runtime" -- and it was not a
/// convention but the absence of one.</b> <c>Calibrator.Apply</c>, the <c>SharpenPipeline</c> steps
/// and <see cref="MaskedBoost"/> each returned either a new image or the caller's own input
/// depending on configuration, so fourteen call sites wrote
/// <c>if (!ReferenceEquals(result, input)) input.Release();</c> longhand. P1 of
/// <c>docs/plans/frame-lifecycle.md</c> retired every one of them. <b>Do not reintroduce that
/// idiom.</b></para>
///
/// <para><b>Ownership is a property of the HANDOFF, not of reference identity</b>, which is why the
/// guard had to go rather than be tidied. <c>ReferenceEquals</c> answers "is this a different
/// instance?", which coincides with "do I own this?" only while a single thread owns the whole chain
/// -- an undocumented precondition of every site that wrote it. Getting it wrong is silent:
/// releasing a frame you did not own recycles pixels another holder is still reading, which surfaces
/// as a corrupted stack rather than an exception. In every case the answer was already in hand one
/// branch earlier (<c>Blend &lt; 1f</c>, <c>channels == 1</c>, <c>options.IsNoOp</c>, the assignment
/// that replaced an accumulator), so the fix was to ask THAT and not the reference. Where a producer
/// cannot offer such a predicate, make it CONSUME its input instead, as <c>Calibrator.Apply</c>
/// does.</para>
///
/// <para><b>Ownership transfer is visible in the name</b>, where the name is otherwise neutral about
/// it: <c>Adopt*</c> and <c>*Into*</c> take ownership of what they are given, a bare
/// <c>CreateFrom*</c> or <c>Get*</c> does not, and <c>*InPlace</c> says an instance method consumes
/// its own receiver. The one deliberate exception is <c>Calibrator.Apply</c>, an established domain
/// verb that consumes its light: renaming it buys a weaker signal than the doc plus
/// <c>CalibratorOwnershipTests</c> already give, so it is documented and pinned instead. Do not treat
/// that as licence for the next one -- it is recorded as an exception in
/// <c>docs/plans/frame-lifecycle.md</c> precisely so it stays one.</para>
///
/// <para><b>"Released" means ownership is spent, and nothing else.</b> Dropping the float planes to
/// save memory is EVICTION (<see cref="TryEvictFloatPlanes"/>, <see cref="PlanesResident"/>) -- a
/// separate, reversible fact with the opposite implication for a caller: an evicted image is
/// perfectly usable and rebuilds itself on the next read, whereas a released one must not be touched
/// at all. The two shared the word "released" until this was written down, which is the single most
/// likely way for a reader of this type to write the inverted guard.</para>
/// </remarks>
public partial class Image(ImmutableArray<Channel> initialChannels, BitDepth bitDepth, float pedestal, ImageMeta imageMeta,
    bool samplesAreUnitReferred = false, ImmutableArray<byte[]> sourceRaster = default)
{
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = ValidateSameShape(initialChannels)[0].Width;

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = initialChannels[0].Height;

    public int ChannelCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = initialChannels.Length;

    public (int ChannelCount, int Width, int Height) Shape => (ChannelCount, Width, Height);

    public BitDepth BitDepth => bitDepth;

    /// <summary>
    /// The samples are unit-referred (1.0 is full scale) even though <see cref="BitDepth"/> names an
    /// INTEGER container -- set by an importer that normalised integer samples on read.
    /// </summary>
    /// <remarks>
    /// <para><b>Two independent facts were riding on one field, and the collision was silent.</b>
    /// <see cref="BitDepth"/> is a statement about the SOURCE (see
    /// <c>BitDepthEx.CarriesDisplayDataOnly</c>, "a statement about FILES"), so a 16-bit PNG must keep
    /// saying Int16. But the importer also normalises its samples to <c>[0, 1]</c>, and
    /// <see cref="IsUnitScaledFloat"/> used to infer THAT from the same field by requiring
    /// <see cref="BitDepth.Float32"/>. So every PNG, JPEG and 8/16-bit TIFF read into the viewer was
    /// classified as ADU data, binned into TWO histogram buckets, and detected ZERO stars -- with no
    /// error anywhere, because the star overlay, HFD/FWHM, Boost, Calibrate and SPCC are all gated on
    /// a non-empty star list and simply went quiet. Measured on a 16-bit PNG carrying 40 planted
    /// Gaussian stars: 0 detected before, 40 after (<c>UnitReferredImportStarDetectionTests</c>).</para>
    /// <para>The rescalers could not paper over it: both early-return <c>this</c> when the peak is
    /// already at or under 1, so an image that arrives normalised never passes through the code that
    /// stamps <see cref="BitDepth.Float32"/>.</para>
    /// <para>A site that forwards another image BitDepth must forward this too -- it is copying "what
    /// scale is this data in", and that answer now has two halves.</para>
    /// </remarks>
    public bool SamplesAreUnitReferred => samplesAreUnitReferred;

    /// <summary>
    /// True when the ORIGINAL 8-bit samples of at least one channel were kept alongside the float
    /// planes, so a viewer can upload them directly instead of the widened floats.
    /// </summary>
    public bool HasSourceRaster => !sourceRaster.IsDefaultOrEmpty;

    /// <summary>
    /// The channels, as one reference that is only ever REPLACED, never edited in place.
    /// </summary>
    /// <remarks>
    /// <para>Residency is <b>derived</b> from this array rather than tracked in a flag beside it. A
    /// flag would be the same fact stored twice, and the two can disagree: whichever order the two
    /// writes land in, a reader can catch the pair mid-update and either read an evicted plane while
    /// the flag still says resident, or restore planes that are already there.</para>
    /// <para>Every transition builds the complete replacement locally and publishes it with ONE
    /// interlocked write, so a concurrent reader sees either the whole before or the whole after --
    /// never a half-restored array with some channels real and some empty. That matters because this
    /// type is documented as immutable and ships in a package: a consumer reading two channels from
    /// two threads is entitled to do so, and cannot be expected to know that a read can rebuild them.</para>
    /// <para><b>Deriving it is the expensive half, and the hoist is what pays for it.</b> Asking
    /// <see cref="IsEvicted"/> costs a second copy of a five-field <see cref="Channel"/> plus a
    /// dependent <c>.Data</c> load and a length check -- nothing once, and <b>+8.7% to +20.3%</b> on
    /// the bilinear resample loops at 12.6M samples for a 2048-square colour pass (`WarpBenchmarks`,
    /// which also shows that D1' itself, a predicted-not-taken bool, cost nothing). That is not an
    /// argument for going back to a flag: the tear a flag permits is in the array, not the flag, so
    /// <c>volatile</c> would not have bought this. It is an argument for
    /// <see cref="ResidentPlanes"/> -- resolve residency ONCE per operation and hand the loop plain
    /// <c>float[,]</c>. Under AOT, which is what ships, that returns to parity.</para>
    /// </remarks>
    private ImmutableArray<Channel> _planes = initialChannels;

    /// <summary>True when <paramref name="planes"/> is the evicted form (every plane a 0x0 stub).</summary>
    private static bool IsEvicted(ImmutableArray<Channel> planes) => planes[0].Data.Length == 0;

    /// <summary>
    /// The channels, with their float planes guaranteed present -- rebuilt from the source raster first
    /// if they were evicted.
    /// </summary>
    /// <remarks>
    /// Every READ accessor goes through here rather than touching <c>channels</c> directly: the indexer,
    /// <see cref="GetChannel"/> and <see cref="GetChannelSpan"/>. A path that bypasses it and reads a
    /// evicted plane meets an empty array and THROWS, which is the deliberate choice -- the alternative
    /// to failing loudly is failing silently, reading zeroes and drawing a plausible black picture. Of the
    /// two ways to be wrong about residency, the one that crashes is the one that gets fixed.
    /// </remarks>
    private ImmutableArray<Channel> Planes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Reading a recycled frame is the one way to be wrong here that used to be SILENT, so it
            // throws. See _recycled for why the check is not simply "_released".
            //
            // The throw lives in a NoInlining helper rather than here: an inline throw carries the
            // string, the ObjectDisposedException construction and its call into this accessor's IL,
            // and the JIT sizes an inlining candidate by that IL. Keeping the cold path out of the
            // body is what lets the whole property fold into its caller, so the check costs one
            // predicted-not-taken branch over a load-acquire. Same shape as the BCL's ThrowHelper.
            if (_recycled)
            {
                ThrowRecycled();
            }

            // ONE reference read, then work off the snapshot: re-reading the field mid-method is how a
            // caller ends up mixing two generations of the array.
            var snapshot = _planes;
            return IsEvicted(snapshot) ? RestorePlanesFromRaster(snapshot) : snapshot;
        }
    }

    /// <summary>
    /// The cold half of the recycled-frame check, kept out of <see cref="Planes"/> so that accessor
    /// stays small enough to inline.
    /// </summary>
    /// <remarks>
    /// <b>Not folded into the residency check, deliberately</b>, although it could be: publishing the
    /// evicted (0x0) plane form from <see cref="Release"/> would make the resident path pay nothing at
    /// all, since <c>IsEvicted</c> is already tested there. That buys one load-acquire on a path that
    /// is per-OPERATION for every loop that hoists <see cref="ResidentPlanes"/> as it must -- and it
    /// pays for it by having ownership write to the residency field, re-entangling the two mutable
    /// facts this type spent P0 separating. Not worth it.
    /// </remarks>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRecycled()
        => throw new ObjectDisposedException(nameof(Image),
            "This frame's arrays went back to the camera or the pool when it was released. "
            + "Take a TryLease if you need the pixels past the owner's release.");

    /// <summary>Whether the float planes are currently resident.</summary>
    public bool PlanesResident => !IsEvicted(_planes);

    /// <summary>
    /// Drops the float planes where the source raster can rebuild them exactly, keeping geometry,
    /// metadata and extrema. False when no raster covers every channel.
    /// </summary>
    /// <remarks>
    /// <para>D1 of <c>docs/plans/viewer-memory-footprint.md</c>. For a document whose source WAS 8-bit
    /// the float planes are pure duplication: the raster holds the same information at 1 B/px and the
    /// plane was widened FROM it, so dropping them is lossless and needs no disk I/O to undo. An 8-bit
    /// RGB document goes from 12 B/px of planes + 3 of raster + 3 on the device, to 3 + 3.</para>
    /// <para>Geometry survives because <see cref="Width"/>, <see cref="Height"/> and
    /// <see cref="ChannelCount"/> are captured at construction rather than read back from the arrays,
    /// which is what makes this a policy rather than a restructure. Per-channel extrema, filter and index
    /// live on the <see cref="Channel"/> record and survive with it; only <c>Channel.Width</c>,
    /// <c>Height</c> and <c>Length</c> read zero while evicted, since those DO read the array.</para>
    /// <para>Refused for a channel carrying a recycled camera <see cref="Channel.Buffer"/>: that array
    /// belongs to the driver pool, and dropping our reference to it would hand back something still in
    /// use.</para>
    /// </remarks>
    public bool TryEvictFloatPlanes()
    {
        // A degenerate 0-px image would satisfy the length check below against a 0-length raster and
        // "evict" planes that are already empty, which then reads as evicted forever.
        var expected = Width * Height;
        if (expected == 0 || sourceRaster.IsDefaultOrEmpty || sourceRaster.Length != ChannelCount)
        {
            return false;
        }

        while (true)
        {
            var snapshot = _planes;
            if (IsEvicted(snapshot))
            {
                return true;
            }

            for (var c = 0; c < snapshot.Length; c++)
            {
                if (sourceRaster[c] is not { } raster || raster.Length != expected
                    || snapshot[c].Buffer is not null)
                {
                    return false;
                }
            }

            var evicted = ImmutableArray.CreateBuilder<Channel>(snapshot.Length);
            for (var c = 0; c < snapshot.Length; c++)
            {
                evicted.Add(snapshot[c] with { Data = EmptyPlane });
            }

            if (ImmutableInterlocked.InterlockedCompareExchange(ref _planes, evicted.MoveToImmutable(), snapshot)
                == snapshot)
            {
                return true;
            }

            // Lost the publication race, so the array we inspected is stale: someone restored (or
            // evicted) underneath us. Re-read and decide again rather than forcing our own answer on
            // top of theirs.
        }
    }

    private static readonly float[,] EmptyPlane = new float[0, 0];

    private ImmutableArray<Channel> RestorePlanesFromRaster(ImmutableArray<Channel> evicted)
    {
        // The raster is the ORIGINAL 8-bit samples and the evicted plane was normalised by the
        // sample-format maximum, so this reproduces the values exactly rather than approximately: the same
        // division over the same bytes. Anything less than exact would show up as a readout that changed
        // after an eviction, which is the one thing this must never do.
        var width = Width;
        var height = Height;
        var restored = ImmutableArray.CreateBuilder<Channel>(evicted.Length);
        for (var c = 0; c < evicted.Length; c++)
        {
            var raster = sourceRaster[c];
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = raster[row + x] / 255f;
                }
            }

            restored.Add(evicted[c] with { Data = plane });
        }

        var built = restored.MoveToImmutable();
        var prior = ImmutableInterlocked.InterlockedCompareExchange(ref _planes, built, evicted);
        if (prior == evicted)
        {
            return built;
        }

        // Another thread published first. Prefer what IS published so every reader agrees on one set of
        // arrays, and let ours be collected: two restorers compute identical values from the same bytes,
        // so duplicating the work is wasteful rather than wrong -- and it is cheaper than making every
        // read take a lock to prevent it. Falling back to our own build if the winner somehow published
        // the evicted form keeps this from ever handing back empty planes.
        var current = _planes;
        return IsEvicted(current) ? built : current;
    }

    /// <summary>
    /// The original 8-bit samples of <paramref name="channel"/>, flat and row-major, matching the
    /// float plane's <c>[Height, Width]</c> layout. False when this image carries none.
    /// </summary>
    /// <remarks>
    /// <para><b>Why keep bytes we already widened:</b> the float plane costs 4 B/px on the host AND
    /// 4 B/px on the GPU, while these bytes cost 1 and let the texture be <c>R8Unorm</c> for a
    /// further 1 B/px there. For a document whose source WAS 8-bit that is not a quality trade at
    /// all -- the float plane was derived from these very bytes, so uploading them is lossless and
    /// skips a re-quantise. See D3' in <c>docs/plans/viewer-memory-footprint.md</c>.</para>
    /// <para><b>Only an importer sets this, and every other construction drops it. That is
    /// deliberate, not an oversight.</b> The raster describes the samples as they were READ; any
    /// transform that rescales, inverts, debayers, stacks or otherwise recomputes pixels makes it
    /// a lie. Because it is an opt-in constructor argument, a transform that builds a fresh image
    /// loses it automatically -- which is the fail-closed direction. Never forward it the way
    /// <see cref="SamplesAreUnitReferred"/> is forwarded: that describes a SCALE, which survives a
    /// copy, whereas this describes SPECIFIC BYTES, which does not.</para>
    /// <para>A shape mismatch is declined rather than thrown, because the alternative to no raster
    /// is a texture uploaded from the wrong number of bytes -- which draws a plausible-looking
    /// wrong picture instead of failing.</para>
    /// </remarks>
    public bool TryGetSourceRaster(int channel, out ReadOnlySpan<byte> raster)
    {
        if (!sourceRaster.IsDefaultOrEmpty
            && (uint)channel < (uint)sourceRaster.Length
            && (uint)channel < (uint)ChannelCount
            && sourceRaster[channel] is { } plane
            && plane.Length == Width * Height)
        {
            raster = plane;
            return true;
        }

        raster = default;
        return false;
    }

    /// <summary>
    /// Image-wide full-scale value, derived as the maximum over the channels' <see cref="Channel.MaxValue"/>.
    /// Legacy raw-array constructions stamp the same image-wide value on every channel, so this reads
    /// back exactly what was passed in; channel-typed constructions keep per-channel maxima intact
    /// (reachable via <see cref="GetChannel"/>).
    /// </summary>
    public float MaxValue { get; } = DeriveMax(initialChannels);

    /// <summary>Image-wide minimum, derived as the minimum over the channels' <see cref="Channel.MinValue"/>.</summary>
    public float MinValue { get; } = DeriveMin(initialChannels);

    /// <summary>
    /// Legacy raw-array overload: wraps each plane in a <see cref="Channel"/> carrying the
    /// image-wide <paramref name="maxValue"/>/<paramref name="minValue"/> (no per-channel stats,
    /// no buffers). Prefer the <see cref="ImmutableArray{T}"/>-of-<see cref="Channel"/> constructor
    /// for new code: it keeps per-channel min/max and lets a camera buffer travel with its channel.
    /// </summary>
    public Image(float[][,] data, BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta imageMeta,
        bool samplesAreUnitReferred = false, ImmutableArray<byte[]> sourceRaster = default)
        : this(WrapRawPlanes(data, minValue, maxValue), bitDepth, pedestal, imageMeta, samplesAreUnitReferred,
            sourceRaster)
    {
    }

    private static ImmutableArray<Channel> WrapRawPlanes(float[][,] data, float minValue, float maxValue)
    {
        var builder = ImmutableArray.CreateBuilder<Channel>(data.Length);
        for (var c = 0; c < data.Length; c++)
        {
            builder.Add(new Channel(data[c], default, minValue, maxValue, (byte)c));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<Channel> ValidateSameShape(ImmutableArray<Channel> channels)
    {
        if (channels.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An image needs at least one channel.", nameof(channels));
        }
        var (h, w) = (channels[0].Height, channels[0].Width);
        for (var c = 1; c < channels.Length; c++)
        {
            if (channels[c].Height != h || channels[c].Width != w)
            {
                throw new ArgumentException(
                    $"Channel {c} is {channels[c].Width}x{channels[c].Height} but channel 0 is {w}x{h}.", nameof(channels));
            }
        }
        return channels;
    }

    // MathF.Max/Min propagate NaN, preserving the legacy behaviour where an image constructed
    // with maxValue = float.NaN (e.g. FromChannel's default) reads MaxValue = NaN.
    private static float DeriveMax(ImmutableArray<Channel> channels)
    {
        var max = channels[0].MaxValue;
        for (var c = 1; c < channels.Length; c++)
        {
            max = MathF.Max(max, channels[c].MaxValue);
        }
        return max;
    }

    private static float DeriveMin(ImmutableArray<Channel> channels)
    {
        var min = channels[0].MinValue;
        for (var c = 1; c < channels.Length; c++)
        {
            min = MathF.Min(min, channels[c].MinValue);
        }
        return min;
    }
    /// <summary>
    /// ADU pedestal added to pixel values to keep them non-negative after
    /// calibration subtraction (see <see cref="ImageMeta"/> remarks for the
    /// OFFSET / PEDESTAL / BZERO distinction). 0 for raw frames; the
    /// calibration <see cref="Subtract"/> path accumulates the user-supplied
    /// offset here so downstream stretch / stats can subtract it back out.
    /// </summary>
    public float Pedestal => pedestal;
    /// <summary>
    /// Image metadata such as instrument, exposure time, focal length, pixel size, ...
    /// </summary>
    public ImageMeta ImageMeta => imageMeta;

    /// <summary>
    /// Computes <see cref="ImageDim"/> from image dimensions and metadata (pixel size, binning, focal length).
    /// </summary>
    /// <returns>Image dimensions with pixel scale, or <c>null</c> if metadata is insufficient.</returns>
    public ImageDim? GetImageDim()
    {
        var meta = ImageMeta;
        if (meta.PixelSizeX > 0 && meta.FocalLength > 0 && meta.BinX > 0)
        {
            var pixelScale = Astrometry.CoordinateUtils.PixelScaleArcsec(meta.PixelSizeX * meta.BinX, meta.FocalLength);
            return new ImageDim(pixelScale, Width, Height);
        }
        return null;
    }

    /// <summary>
    /// Read-only indexer to get a pixel value.
    /// </summary>
    /// <param name="h"></param>
    /// <param name="w"></param>
    /// <returns></returns>
    public float this[int c, int h, int w] => Planes[c].Data[h, w];

    /// <summary>
    /// Returns the typed <see cref="Channel"/> for a plane; per-channel filter/min/max travel here
    /// (the image-wide <see cref="MaxValue"/>/<see cref="MinValue"/> are the derived extrema).
    /// </summary>
    public Channel GetChannel(int channel) => Planes[channel];

    /// <summary>
    /// Returns a flat span over the pixel data for a single channel plane (height * width floats).
    /// </summary>
    public ReadOnlySpan<float> GetChannelSpan(int channel)
    {
        var plane = Planes[channel].Data;
        return MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
    }

    /// <summary>
    /// Returns the raw backing <c>float[,]</c> for a channel. Internal; use for low-level
    /// interop (guider tracker, FITS write) where span access is insufficient.
    /// </summary>
    internal float[,] GetChannelArray(int channel) => Planes[channel].Data;

    /// <summary>
    /// Wraps a single mono <c>float[,]</c> channel in an <see cref="Image"/> with default metadata.
    /// Convenience for guider frames and test helpers.
    /// </summary>
    public static Image FromChannel(float[,] channel, float maxValue = float.NaN, float minValue = float.NaN)
        => new Image([channel], BitDepth.Float32, maxValue, minValue, 0f, new ImageMeta { SensorType = SensorType.Monochrome });

    /// <summary>
    /// SIMD-accelerated element-wise multiply: <c>dst[i] = src[i] * scalar</c>.
    /// Supports in-place operation (src and dst may alias).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MultiplyScalar(ReadOnlySpan<float> src, float scalar, Span<float> dst)
        => TensorPrimitives.Multiply(src, scalar, dst);

    /// <summary>
    /// Creates a jagged channel array structure: an array of 2D float arrays, one per channel.
    /// This avoids a single huge LOH allocation for multi-channel images.
    /// </summary>
    internal static float[][,] CreateChannelData(int channelCount, int height, int width)
    {
        var channels = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            channels[c] = new float[height, width];
        }
        return channels;
    }

    /// <summary>
    /// Ref-counted channel buffers, harvested from the channels' <see cref="Channel.Buffer"/> at
    /// construction: set when the image wraps camera-owned data (the buffer travels WITH its
    /// channel; there is no attach-after-construct step). Null for images whose channels own their
    /// arrays outright (debayer/normalize output, tests, file loads).
    /// </summary>
    private ChannelBuffer?[]? _channelBuffers = HarvestBuffers(initialChannels);

    /// <summary>
    /// Whether <see cref="Release"/> has run. Distinct from a null <see cref="_channelBuffers"/>, which
    /// is also how an image that never carried recyclable buffers looks - see <see cref="TryLease"/>,
    /// which is the only thing that needs to tell those two apart.
    /// </summary>
    private volatile bool _released;

    /// <summary>
    /// Set when <see cref="Release"/> actually handed arrays back, which makes every subsequent read
    /// through <see cref="Planes"/> throw instead of returning pixels somebody else now owns.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not simply <see cref="_released"/>.</b> Release is a no-op for a
    /// self-owned frame, and convention 2's "release the image and go on reading it" call sites are
    /// correct -- an invariant of <c>docs/plans/frame-lifecycle.md</c> and pinned by
    /// <c>FitsPooledReadTests.UnpooledRead_CarriesNoBuffer_SoReleaseStaysANoOp</c>. Throwing on
    /// <see cref="_released"/> would break every one of them. What is never correct is reading a
    /// frame whose arrays went back to a camera or a pool, and that is exactly the case where
    /// <c>HarvestBuffers</c> found something to release.</para>
    /// <para><b>Why the check had to move here at all.</b> <see cref="ChannelBuffer.Data"/> has
    /// guarded itself since it was written -- and has never had a single call site, because every
    /// pixel read in the tree goes through <see cref="Channel.Data"/>, a plain field. So the loud
    /// failure the policy claimed for conventions 1 and 3 did not exist: reading a released camera
    /// frame returned whatever the driver had since put in that array. One choke point, one volatile
    /// read, at frame granularity for any loop that hoists <see cref="ResidentPlanes"/> as it must.</para>
    /// <para>Set BEFORE the buffers are released, so a racing reader cannot slip between the handback
    /// and the poison.</para>
    /// </remarks>
    private volatile bool _recycled;

    private static ChannelBuffer?[]? HarvestBuffers(ImmutableArray<Channel> channels)
    {
        ChannelBuffer?[]? buffers = null;
        for (var c = 0; c < channels.Length; c++)
        {
            if (channels[c].Buffer is { } buffer)
            {
                buffers ??= new ChannelBuffer?[channels.Length];
                buffers[c] = buffer;
            }
        }
        return buffers;
    }

    /// <summary>
    /// Spends this image's ownership: releases all ref-counted channel buffers, and once the last
    /// holder releases, the backing <c>float[,]</c> goes back to the camera (convention 1) or to
    /// <c>Array2DPool</c> (convention 3) for reuse. A no-op for a self-owned frame (convention 2).
    /// Safe to call multiple times: idempotent.
    /// </summary>
    /// <remarks>
    /// This is the OWNERSHIP verb and the only thing "released" means on this type. Dropping the float
    /// planes to save memory is <see cref="TryEvictFloatPlanes"/> instead -- reversible, and it leaves
    /// the image perfectly usable. See the frame-ownership notes on <see cref="Image"/> for which
    /// convention a given frame arrived under.
    /// </remarks>
    public void Release()
    {
        // Set before the exchange so a concurrent TryLease that reads a null buffer array cannot then
        // read a stale "not released yet" and hand out planes this call is about to give back.
        _released = true;

        if (Interlocked.Exchange(ref _channelBuffers, null) is { } buffers)
        {
            // Before the handback, not after: a reader racing this call must meet the exception
            // rather than the window in which the array is already somebody else's.
            _recycled = true;

            for (var c = 0; c < buffers.Length; c++)
            {
                buffers[c]?.Release();
            }
        }
    }

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    /// <remarks>
    /// Convenience for a caller that samples a handful of points. Anything sampling PER PIXEL must
    /// hoist <see cref="ResidentPlanes"/> out of its loop and use the plane-taking overload instead:
    /// resolving residency is a struct copy, a dependent load and a length check, which is nothing per
    /// star and 12.6M times nothing for a 2048-square colour warp -- measured at +8.7% to +20.3% on
    /// those loops before the hoist (`WarpBenchmarks`).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(int channel, float x1, float y1)
        => SubpixelValue(Planes[channel].Data, x1, y1);

    /// <summary>
    /// Every channel's plane with residency resolved ONCE, for a loop that samples per pixel.
    /// </summary>
    /// <remarks>
    /// The array is a snapshot: it holds the planes themselves, so it cannot be invalidated by a
    /// release landing mid-loop the way a re-read of the channel array could. That is deliberate --
    /// a guarantee of residency must not outlive the operation that established it, and holding the
    /// arrays is what makes the guarantee true rather than merely claimed.
    /// </remarks>
    private float[][,] ResidentPlanes()
    {
        var planes = Planes;
        var arrays = new float[planes.Length][,];
        for (var c = 0; c < planes.Length; c++)
        {
            arrays[c] = planes[c].Data;
        }

        return arrays;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(float[,] channelData, float x1, float y1)
    {
        var width = Width;
        var height = Height;

        // assumes that maxVal < long.MaxValue
        var x_trunc = (long)MathF.Truncate(x1);
        var y_trunc = (long)MathF.Truncate(y1);

        if (x_trunc < 0 || x_trunc >= width || y_trunc < 0 || y_trunc >= height)
        {
            return float.NaN;
        }
        else if (x_trunc == x1 && y_trunc == y1)
        {
            return channelData[y_trunc, x_trunc];
        }

        var x_frac = x1 - x_trunc;
        var y_frac = y1 - y_trunc;
        try
        {
            const int tl = 0;
            const int tr = 1;
            const int bl = 2;
            const int br = 3;

            byte mask = 0;
            Span<float> pixels = stackalloc float[4];
            pixels.Fill(float.NaN);

            pixels[tl] = channelData[y_trunc, x_trunc];
            if (x_trunc < width - 1)
            {
                pixels[tr] = channelData[y_trunc, x_trunc + 1];
            }

            if (y_trunc < height - 1)
            {
                pixels[bl] = channelData[y_trunc + 1, x_trunc];
            }

            if (x_trunc < width - 1 && y_trunc < height - 1)
            {
                pixels[br] = channelData[y_trunc + 1, x_trunc + 1];
            }

            for (var i = 0; i < 4; i++)
            {
                if (!float.IsNaN(pixels[i]))
                {
                    mask |= (byte)(1 << i);
                }
            }

            if ((mask & 0b1111) == 0b1111)
            {
                return pixels[tl] * (1 - x_frac) * (1 - y_frac)
                    + pixels[tr] * x_frac * (1 - y_frac)
                    + pixels[bl] * (1 - x_frac) * y_frac
                    + pixels[br] * x_frac * y_frac;
            }
            else
            {
                int main;
                if (x_frac <= 0.5f && y_frac <= 0.5f)
                {
                    main = tl;
                }
                else if (x_frac > 0.5f && y_frac <= 0.5f)
                {
                    main = tr;
                }
                else if (x_frac <= 0.5f && y_frac > 0.5f)
                {
                    main = bl;
                }
                else
                {
                    main = br;
                }

                // if the main pixel is not lit, return NaN
                if ((mask & (1 << main)) == (1 << main))
                {
                    return pixels[main];
                }
                // for now, return NaN if any non-main pixel is NaN, a better approach would be to interpolate using only the available pixels
                else
                {
                    return float.NaN;
                }
            }
        }
        catch (Exception ex) when (Environment.UserInteractive)
        {
            GC.KeepAlive(ex);
            throw;
        }
        catch
        {
            return float.NaN;
        }
    }

    /// <summary>
    /// Scales the floating-point values of the image data so the unit-space FULL SCALE (1.0) maps to
    /// <paramref name="newMaxValue"/>.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <remarks>This method is intended for images that have been obtained via <see cref="ScaleFloatValuesToUnit"/> floating-point data (i.e., Float32 bit
    /// depth and values in [0, 1]). If the image is already denormalized or uses a different bit depth, no
    /// scaling is performed. Note the mapping is full-scale-to-full-scale, not peak-to-peak: a
    /// full-scale-normalised image (see <see cref="UnitScaleDivisor"/>) whose observed peak sits below 1.0
    /// lands with its peak proportionally below <paramref name="newMaxValue"/> -- which round-trips the
    /// original ADU values, rather than stretching the frame's own peak to <paramref name="newMaxValue"/>.</remarks>
    /// <param name="newMaxValue">The value the unit-space full scale (1.0) is mapped to. Must be greater than zero.</param>
    /// <returns>An Image instance containing the denormalized data. If the
    /// image is already denormalized or not in Float32 bit depth, the original image is returned unchanged.</returns>
    public Image ScaleFloatValues(float newMaxValue, float missingValue = float.NaN)
    {
        if (BitDepth != BitDepth.Float32 || (newMaxValue != MaxValue && !HasUnitScalePeak))
        {
            return ScaleFloatValuesToUnit().ScaleFloatValues(newMaxValue);
        }

        var (channelCount, width, height) = Shape;
        var denormalized = CreateChannelData(channelCount, height, width);

        for (var c = 0; c < channelCount; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref denormalized[c][0, 0], denormalized[c].Length);
            MultiplyScalar(src, newMaxValue, dst);
        }

        // The stamped MaxValue is the OBSERVED peak scaled by the same factor as the pixels (for a
        // legacy peak-normalised input MaxValue == 1 this is exactly newMaxValue, as before).
        return new Image(denormalized, BitDepth.Float32, MaxValue * newMaxValue, MinValue * newMaxValue, pedestal * newMaxValue, RescaleMeta(newMaxValue));
    }

    /// <summary>
    /// Divides image by the sensor's fixed ADU full-scale when known (<see cref="ImageMeta.SensorFullScaleAdu"/>),
    /// otherwise by <see cref="MaxValue"/> -- scaling the floating-point values into <c>[0, 1]</c>. A live
    /// camera capture normalises against its sensor's true saturation point rather than its own observed
    /// peak, so an under-exposed frame correctly lands below 1.0 instead of always stretching its own max
    /// to exactly 1.0; a source without that metadata (file imports, calibration masters, ...) falls back
    /// to the prior observed-peak behaviour unchanged.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <returns></returns>
    public Image ScaleFloatValuesToUnit(float missingValue = float.NaN)
    {
        // NO-OP for already normalized images
        // Already unit-referred: return the image untouched rather than paying a full pass to divide
        // by something indistinguishable from 1. On a 25 MP master that pass is not free.
        if (HasUnitScalePeak)
        {
            return this;
        }

        var (channelCount, width, height) = Shape;
        var normalized = CreateChannelData(channelCount, height, width);
        var invMax = 1.0f / UnitScaleDivisor;

        for (var c = 0; c < channelCount; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref normalized[c][0, 0], normalized[c].Length);
            MultiplyScalar(src, invMax, dst);
        }

        return new Image(normalized, BitDepth.Float32, MaxValue * invMax, MinValue * invMax, pedestal * invMax, RescaleMeta(invMax),
            samplesAreUnitReferred: true);
    }

    /// <summary>
    /// The divisor the canonical [0, 1] normalisation uses: the sensor's fixed ADU full-scale when
    /// known (<see cref="ImageMeta.SensorFullScaleAdu"/>, e.g. from a live camera's MaxADU or a FITS
    /// SATURATE card) so the conversion is stable across frames, otherwise the observed peak
    /// (<see cref="MaxValue"/>). Never below the observed peak -- a hot pixel or calibration artifact
    /// above the nominal full-scale must not map above 1.0. Single source of truth shared by
    /// <see cref="ScaleFloatValuesToUnit"/>, <see cref="ScaleFloatValuesToUnitInPlace"/>, and the
    /// TIFF export normalisation (a private divisor in any one of them drifts out of agreement with
    /// the others -- the TiffRoundTripTests comparison is the regression guard).
    /// </summary>
    internal float UnitScaleDivisor => imageMeta.SensorFullScaleAdu is { } adu ? MathF.Max(adu, MaxValue) : MaxValue;

    /// <summary>
    /// How far above 1.0 a peak may sit and still count as unit-referred.
    /// </summary>
    /// <remarks>
    /// A tolerance is required, not merely nice: the exact maximum is not a property of the SCALE, it
    /// is one pixel. A quantized <c>.fz</c> decode is 1-ulp noisy, so a master written normalised reads
    /// back a hair over one -- and on the frame that exposed this, exactly ONE pixel of 24.9 million
    /// exceeded 1.0, by 15 ulps, in the BLUE channel, while <see cref="MaxValue"/> is image-wide, so it
    /// changed how the RED channel was measured. This bound sits four orders of magnitude above that
    /// noise and five below the nearest competing scale (8-bit, 255), so it cannot confuse the two.
    /// </remarks>
    private const float UnitScaleTolerance = 1e-3f;

    /// <summary>
    /// Is the peak at or near 1.0 -- i.e. are the samples unit-referred rather than ADU?
    /// </summary>
    /// <remarks>
    /// The ONE answer to that question. It used to be asked in two places in two spellings --
    /// <c>Image.Histogram</c>'s <c>MaxValue &lt;= 1.0f</c> and
    /// <c>AstroImageDocument.AdoptImageAsync</c>'s <c>MaxValue &gt; 1.0f + float.Epsilon</c> -- which
    /// agreed only because they were exact complements of an exact test. Give a tolerance to one and
    /// not the other and a band opens up where an image is left un-normalised AND refused the histogram
    /// rescale, which fails the way described on <see cref="IsUnitScaledFloat"/>.
    /// </remarks>
    internal bool HasUnitScalePeak => IsUnitScalePeak(MaxValue);

    /// <summary>
    /// <see cref="HasUnitScalePeak"/> for a peak that has been passed around on its own.
    /// </summary>
    /// <remarks>
    /// <see cref="StretchSolver"/> receives the max value rather than the image, and its answer has to
    /// match the image's: it produces the shader's NormFactor, while <see cref="Histogram"/> picks the
    /// divisor the CPU statistics are expressed in. The two disagreeing is a display that does not
    /// match its own histogram.
    /// </remarks>
    internal static bool IsUnitScalePeak(float maxValue) => maxValue <= 1.0f + UnitScaleTolerance;

    /// <summary>
    /// Float data already in <c>[0, 1]</c>, so a histogram may bin it into 65535 buckets.
    /// </summary>
    /// <remarks>
    /// Getting this wrong is silent and total: [0,1] samples binned at face value land in TWO bins, so
    /// <see cref="Background"/> reports a background of 0, so <c>FindStarsAsync</c> takes its "abnormal
    /// file" path and returns NO stars. No error, no warning, an empty list.
    /// </remarks>
    internal bool IsUnitScaledFloat => HasUnitScalePeak && (BitDepth is BitDepth.Float32 || samplesAreUnitReferred);

    /// <summary>
    /// The channel to detect stars in: green on a 3-channel image, channel 0 otherwise.
    /// </summary>
    /// <remarks>
    /// <para>Green because it carries twice the CFA sampling of red or blue, so it detects the most
    /// stars -- and because red is where the emission lives. An Ha-bright target puts most of its
    /// nebulosity in red, whose structure the detector reports as stars: measured on a Bubble Nebula
    /// master, red's MAD is 12.2 against 6.2 and 6.4 for green and blue, and a plate solve from red
    /// matched 1 of 102 detections to the catalog (rejected as noise) while green matched 10 of 109
    /// and blue 11 of 116, agreeing on the answer to about an arcsecond.</para>
    /// <para>Luminance is NOT the answer despite being 71% green by weight -- the same frame solves
    /// from green and blue and fails from a Rec.709 luma at 0 of 109, so the red term is enough to
    /// poison it. Measured, not assumed.</para>
    /// <para>Returns 0 for anything under 3 channels, which is what makes routing every caller through
    /// this safe: a mono frame or a Bayer MOSAIC (1 channel, debayered inside
    /// <see cref="FindStarsAsync"/>) is unaffected.</para>
    /// </remarks>
    public int ReferenceStarChannel => ReferenceStarChannelFor(ChannelCount);

    /// <inheritdoc cref="ReferenceStarChannel"/>
    public static int ReferenceStarChannelFor(int channelCount) => channelCount >= 3 ? 1 : 0;

    /// <summary>
    /// Convenience over <see cref="ImageMeta.Rescale"/> (the single implementation): rescales the
    /// scale-dependent metadata by the same factor applied to the pixel values. Without this,
    /// writing a normalised image to FITS would stamp a stale ADU-scale SATURATE against [0,1] data.
    /// </summary>
    private ImageMeta RescaleMeta(float pixelScaleFactor)
    {
        return imageMeta.Rescale(pixelScaleFactor);
    }

    /// <summary>
    /// In-place version of <see cref="ScaleFloatValuesToUnit"/>: divides all pixel values by the sensor's
    /// fixed ADU full-scale when known, otherwise by <see cref="MaxValue"/> (see <see cref="ScaleFloatValuesToUnit"/>),
    /// mutating the underlying channel arrays. Returns a new <see cref="Image"/> wrapping the same arrays.
    /// </summary>
    /// <remarks>
    /// <para>Internal only: callers must ensure the source image is not retained elsewhere.</para>
    /// <para><b>Frame ownership: convention 4, a CONSUMED input.</b> The result shares this image's
    /// arrays and carries <c>Buffer = null</c>, so release responsibility stays with this instance and
    /// the input must not be used again. See the frame-ownership notes on <see cref="Image"/>.</para>
    /// </remarks>
    internal Image ScaleFloatValuesToUnitInPlace(float missingValue = float.NaN)
    {
        // Already unit-referred: return the image untouched rather than paying a full pass to divide
        // by something indistinguishable from 1. On a 25 MP master that pass is not free.
        if (HasUnitScalePeak)
        {
            return this;
        }

        var invMax = 1.0f / UnitScaleDivisor;

        // Through Planes, and ONCE: this mutates the arrays in place, so it must operate on resident
        // planes (an evicted channel is a 0x0 stub and plane[0, 0] would throw) and the rewrap below
        // has to carry the very same arrays it just scaled.
        var planes = Planes;
        for (var c = 0; c < ChannelCount; c++)
        {
            // NaN * invMax = NaN, so NaN values are preserved without branching.
            var plane = planes[c].Data;
            var span = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            MultiplyScalar(span, invMax, span);
        }

        // Rewrap the SAME arrays with rescaled per-channel min/max. Buffer deliberately NOT
        // carried over: the ref-counted release responsibility stays with the original Image
        // (callers treat the source as consumed but its Release() still owns the recycle); 
        // carrying the ref here would double-release a refcount-1 buffer.
        var rescaled = ImmutableArray.CreateBuilder<Channel>(planes.Length);
        foreach (var channel in planes)
        {
            rescaled.Add(channel with
            {
                MinValue = channel.MinValue * invMax,
                MaxValue = channel.MaxValue * invMax,
                Buffer = null,
            });
        }

        return new Image(rescaled.MoveToImmutable(), BitDepth.Float32, pedestal * invMax, RescaleMeta(invMax),
            samplesAreUnitReferred: true);
    }
}
