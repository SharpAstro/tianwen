using System;
using System.Collections.Generic;
using TianWen.Lib.Geometry;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// The largest axis-aligned rectangle containing no pixel that no frame covered, i.e. what is left of
    /// a stacked master once its canvas ring is discarded. The whole frame when there is nothing to
    /// discard, and <see cref="PixelRect.Empty"/> for an image with no covered pixel at all.
    /// </summary>
    /// <remarks>
    /// <para><b>A pixel counts as absent when it is unusable AND reachable from the border.</b> Unusable
    /// is recognised two ways, because two producers write it differently: an integration leaves the
    /// canvas at exact zero where no frame reached (TianWen and Astro Pixel Processor both do), while a
    /// tool that flags absence explicitly writes NaN. Zero has to hold in EVERY channel, since a zero in
    /// one channel of three is a dead pixel or a genuinely black one; NaN in ANY channel is enough,
    /// since it makes the pixel unusable whatever the others say. That asymmetry is about recognising
    /// the pixel, and it is the whole of the difference: what each then has to prove is the same.</para>
    /// <para><b>The border-reachability half is what separates a canvas ring from everything else, and
    /// it is not a refinement but the difference between an answer and nonsense.</b> A ring touches the
    /// border by construction. An island inside the frame never does, and is something else with its own
    /// cause: a pixel some calibration clipped where it is zero, a hole no drizzle drop's footprint
    /// reached where it is NaN. Both have been measured on the file that reported them, and both are
    /// catastrophic rather than merely wrong, because this is a largest RECTANGLE and it has to thread
    /// between them. 6230 exact zeros, 0.0102% of the pixels, 6108 of them interior and spread over 3607
    /// of 6388 rows, reduced a 9576 x 6388 calibrated sub to 2922 x 949; that shipped in 7.1.1627 as
    /// "auto-crop crops way too much". 1,856 NaN in 20 components reduced a 3024 x 3025 Bayer-drizzle
    /// master to 0.528 of its canvas, columns 10 to 1638, on a frame 99.94% covered (issue #250).</para>
    /// <para><b>NaN was exempt from that test until 8.0, on the reasoning that "NaN is unambiguous".</b>
    /// It is unambiguous about the PIXEL being unusable, and says nothing about WHY, which is the only
    /// question being asked here. 53 of the 79 masters in one bake carry interior holes, every
    /// <c>BayerDrizzle</c> one of them, so the exempt case was the common case rather than the exotic
    /// one. Pinned now by <c>AnInteriorNaNIsADrizzleHoleAndNotAbsence</c> and, for the half that did not
    /// change, <c>ANaNReachingTheBorderIsAbsent</c>.</para>
    /// <para><b>The rectangle keeping a hole is not the same as a consumer coping with one</b>, which is
    /// why this changed alongside <see cref="FillInteriorHolesInPlace"/> rather than on its own: what
    /// survives the crop here still has NaN in it, and a render, a statistic or an enhance input each
    /// has to be given a number.</para>
    /// <para><b>This is the UNION, and the name says intersection.</b> Absence marks where NO frame
    /// reached, so the answer is the largest rectangle inside the area at least one sub covered -- inside
    /// which a band that fewer subs reached survives, with no zeros in it and up to 60% more noise. That
    /// band is <see cref="CoverageEdgeWalk"/>'s business (an estimate, for a frame someone else
    /// produced), or <see cref="LargestCoveredRectangle(Image, double)"/>'s (exact, whenever a coverage
    /// plane exists). Kept as it is because it is the right question for absence itself, and because it
    /// is the starting rectangle both of those refine.</para>
    /// <para><b>The largest RECTANGLE, not the bounding box of the covered pixels</b>, and a real file is
    /// what settles that rather than the argument. On a 3073 x 3085 Astro Pixel Processor composite,
    /// 97,589 pixels (1.029%) are exact zero in all three channels and the absent ones <b>touch every
    /// edge</b>: they span rows 0 to 3084 and columns 0 to 3072. The bounding box of the covered pixels is
    /// therefore the entire frame, ring included. The answer here is 2987 x 3061 at (25, 8), 96.45% of the
    /// frame.</para>
    /// <para>One pass of the standard largest-rectangle-under-a-histogram scan: per row, each column's run
    /// of covered pixels is a bar, and a monotonic stack finds the widest rectangle under those bars.
    /// O(width x height) time and O(width) extra space. Channels are read one at a time down the outer
    /// loop, so every read is sequential and nothing the size of the image is allocated.</para>
    /// <para>Not computed at load: this reads every channel of every pixel, which is tens of milliseconds
    /// on a full-frame master, so it belongs behind an action rather than on the path every document
    /// takes.</para>
    /// </remarks>
    public PixelRect LargestCoveredRectangle()
    {
        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0 || ChannelCount <= 0)
        {
            return PixelRect.Empty;
        }

        var absent = ScanAbsence().Absent;

        return LargestRectangle(width, height, (int y, Span<bool> covered) =>
        {
            for (var x = 0; x < width; x++)
            {
                covered[x] = !absent[y, x];
            }
        });
    }

    /// <summary>
    /// Which pixels are absent: unusable AND reachable from the border. One implementation, because the
    /// crop and the hole fill are the same question asked from opposite sides and must never disagree
    /// about where the canvas ring ends.
    /// </summary>
    /// <param name="trackNaN">Also return WHICH pixels carry a NaN, and give up after the classify pass
    /// when there are none. The classify pass already knows both, so asking costs one bit per pixel and
    /// no extra reads; re-deriving it afterwards would mean a second walk of every pixel of every
    /// channel. A caller that only wants holes has its answer at that point -- there are none -- and the
    /// flood is pure waste, which on a frame with no NaN is most of the method. Off for the crop, which
    /// needs the flood whether or not a NaN was seen.</param>
    private AbsenceScan ScanAbsence(bool trackNaN = false)
    {
        var width = Width;
        var height = Height;
        var channels = ChannelCount;

        // One bit per pixel per set. About 1.1 MB each on this frame and 7.6 MB on a 61 MP one, against
        // the 244 MB the coverage-plane tier already decodes for the same question.
        var absent = new BitMatrix(height, width);
        var candidate = new BitMatrix(height, width);
        var nan = trackNaN ? new BitMatrix(height, width) : default;
        var sawNaN = false;

        var wordsPerRow = candidate.WordsPerRow;

        // One ROW of scratch each. They exist because of the channel-OUTER loop below: the planes are
        // read one at a time so every read is sequential, and that is what makes a column's verdict
        // have to survive the channel loop.
        //
        // A byte per column, measured rather than assumed. These are written once per pixel per channel
        // -- 27.4M times on the frame this was measured on -- and a byte store is the cheapest write
        // there is, where a bit would be a read-modify-write with a shift and a mask. Bit-packing them
        // took the pass from 28.1 ms to 72.5 ms, because the win from packing is memory traffic and
        // there is none to win back at 3 KB: it is in L1 either way.
        var anyNonZero = new bool[width];
        var rowNaN = new bool[width];

        // Pass 1: classify. Both kinds of unusable pixel are only a CANDIDATE here, settled by the flood
        // below; what differs is how each is recognised, not what it then has to prove.
        for (var y = 0; y < height; y++)
        {
            Array.Clear(anyNonZero);
            Array.Clear(rowNaN);

            var rowStart = y * width;
            for (var c = 0; c < channels; c++)
            {
                // ONCE per channel per row. Resolving this per 64-column block instead cost 22 ms on
                // the frame below, because every call re-resolves plane residency (see Image.Planes).
                var plane = GetChannelSpan(c).Slice(rowStart, width);
                for (var x = 0; x < width; x++)
                {
                    var v = plane[x];
                    if (float.IsNaN(v))
                    {
                        rowNaN[x] = true;
                    }
                    else if (v != 0f)
                    {
                        anyNonZero[x] = true;
                    }
                }
            }

            // The row's verdict goes out a WORD at a time, assembled in a register and stored once per
            // 64 columns. The bits were previously set one at a time straight into the plane, which is
            // 3024 read-modify-writes of a word per row where this is 48 stores.
            var candidateRow = candidate.RowWords(y);
            var nanRow = trackNaN ? nan.RowWords(y) : default;
            for (var w = 0; w < wordsPerRow; w++)
            {
                var x0 = w << 6;
                var count = Math.Min(64, width - x0);

                var nanBits = 0ul;
                var candidateBits = 0ul;
                for (var i = 0; i < count; i++)
                {
                    var bit = 1ul << i;
                    if (rowNaN[x0 + i])
                    {
                        nanBits |= bit;
                        candidateBits |= bit;
                    }
                    else if (!anyNonZero[x0 + i])
                    {
                        candidateBits |= bit;
                    }
                }

                // Columns past the last are padding and must stay clear, or the flood walks off the end
                // of the row and every popcount over the plane lies. count < 64 only in the last word.
                candidateRow[w] = candidateBits;
                if (trackNaN)
                {
                    nanRow[w] = nanBits;
                }

                if (nanBits != 0)
                {
                    sawNaN = true;
                }
            }
        }

        // Nothing more to learn for a hole hunt that found no NaN, and the flood is the larger half of
        // what is left.
        if (trackNaN && !sawNaN)
        {
            return new AbsenceScan(absent, nan, false);
        }

        // Pass 2: a canvas ring reaches the border by construction, so only a region CONNECTED to the
        // border is absence. An island inside the frame is something else entirely -- a clipped pixel
        // where it is zero, a drizzle hole where it is NaN -- and treating one as absence is
        // catastrophic rather than merely wrong, because this is a largest RECTANGLE. Both were
        // measured on the files that reported them: 6230 scattered zeros (0.0102% of the pixels) on a
        // 9576 x 6388 calibrated sub took the answer to 2922 x 949, or 4.5% of the frame; 1,856 NaN in
        // 20 components on a 3024 x 3025 Bayer-drizzle master took it to 0.528 of the canvas, the left
        // half of a frame that is 99.94% covered (issue #250).
        //
        // ALTERNATING SWEEPS, 64 COLUMNS AT A TIME. A queue of pixel indices is unbounded (244 MB in
        // the worst case at this frame size) where a sweep needs no extra memory at all; a ring settles
        // in two, the cap only binds on a shape that spirals, and stopping early UNDER-marks absence,
        // which keeps more of the frame rather than cropping more of it.
        //
        // Each sweep is two word operations rather than a walk of pixels. Propagation ACROSS rows is
        // exactly `absent |= candidate & absentOfTheRowBefore`, which is a word AND and a word OR and
        // nothing else. Propagation ALONG a row is the one part that is genuinely sequential -- a bit
        // becomes absent because its neighbour did -- and that is what KoggeStone below turns into six
        // shifts per word instead of sixty-four dependent steps, with a single carry bit crossing each
        // word boundary.
        FloodFromBorder(candidate, absent, width, height);

        return new AbsenceScan(absent, trackNaN ? nan : null, sawNaN);
    }

    /// <summary>
    /// Marks in <paramref name="absent"/> every bit of <paramref name="candidate"/> that is CONNECTED
    /// TO THE BORDER, four-way, leaving interior islands clear.
    /// </summary>
    /// <remarks>
    /// <para>The rule both coverage tiers answer to, which is why it is a method and not a passage
    /// inside one of them. A canvas ring reaches the border by construction, so only a region connected
    /// to the border is absence; an island inside the frame is something else entirely, and treating one
    /// as absence is catastrophic rather than merely wrong, because the consumer is a largest
    /// RECTANGLE. Measured three times on the files that reported it: 6230 scattered zeros (0.0102% of
    /// the pixels) on a 9576 x 6388 calibrated sub took the answer to 2922 x 949, or 4.5% of the frame;
    /// 1,856 NaN in 20 components on a 3024 x 3025 Bayer-drizzle master took it to 0.528 of the canvas
    /// (issue #250); and on the Great Orion Nebula master ONE 96 x 112 cluster of under-weighted blocks
    /// at the Trapezium, 35 blocks of 35,910, took the coverage tier to 51.8% of a canvas whose blocks
    /// pass at 97.3%.</para>
    /// <para><b>Alternating sweeps, 64 columns at a time.</b> A queue of pixel indices is unbounded
    /// (244 MB in the worst case at this frame size) where a sweep needs no extra memory at all; a ring
    /// settles in two, the cap only binds on a shape that spirals, and stopping early UNDER-marks
    /// absence, which keeps more of the frame rather than cropping more of it.</para>
    /// <para>Each sweep is two word operations rather than a walk of pixels. Propagation ACROSS rows is
    /// exactly <c>absent |= candidate &amp; absentOfTheRowBefore</c>, which is a word AND and a word OR
    /// and nothing else. Propagation ALONG a row is the one part that is genuinely sequential (a bit
    /// becomes absent because its neighbour did), and that is what <see cref="KoggeStoneFillUp"/> turns
    /// into six shifts per word instead of sixty-four dependent steps, with a single carry bit crossing
    /// each word boundary.</para>
    /// </remarks>
    private static void FloodFromBorder(BitMatrix candidate, BitMatrix absent, int width, int height)
    {
        var wordsPerRow = candidate.WordsPerRow;
        var lastWord = wordsPerRow - 1;
        var lastColumnBit = 1ul << ((width - 1) & 63);

        const int maxSweeps = 64;
        for (var sweep = 0; sweep < maxSweeps; sweep++)
        {
            var changed = false;

            // Forward: seeds from the border, then down from the row above, then left to right.
            for (var y = 0; y < height; y++)
            {
                var a = absent.RowWords(y);
                var c = candidate.RowWords(y);
                var wholeRowIsBorder = y == 0 || y == height - 1;
                var above = wholeRowIsBorder ? default : absent.RowWords(y - 1);

                var carry = 0ul;
                for (var w = 0; w <= lastWord; w++)
                {
                    var before = a[w];
                    var mask = c[w];
                    var seed = before;

                    if (wholeRowIsBorder)
                    {
                        seed |= mask;
                    }
                    else
                    {
                        seed |= mask & above[w];
                    }

                    // The first and last COLUMN are border too, on every row.
                    if (w == 0)
                    {
                        seed |= mask & 1ul;
                    }

                    if (w == lastWord)
                    {
                        seed |= mask & lastColumnBit;
                    }

                    // The column just left of this word ended up absent, so this word's column 0 is
                    // reachable if it is a candidate.
                    seed |= mask & carry;

                    seed = KoggeStoneFillUp(seed, mask);

                    if (seed != before)
                    {
                        a[w] = seed;
                        changed = true;
                    }

                    carry = seed >> 63;
                }
            }

            // Backward: up from the row below, then right to left. No border seeding -- the forward
            // sweep has already placed every seed there is.
            for (var y = height - 1; y >= 0; y--)
            {
                var a = absent.RowWords(y);
                var c = candidate.RowWords(y);
                var below = y == height - 1 ? default : absent.RowWords(y + 1);

                var carry = 0ul;
                for (var w = lastWord; w >= 0; w--)
                {
                    var before = a[w];
                    var mask = c[w];
                    var seed = before;

                    if (!below.IsEmpty)
                    {
                        seed |= mask & below[w];
                    }

                    if (carry != 0)
                    {
                        seed |= mask & (1ul << 63);
                    }

                    seed = KoggeStoneFillDown(seed, mask);

                    if (seed != before)
                    {
                        a[w] = seed;
                        changed = true;
                    }

                    carry = seed & 1ul;
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Spreads every set bit of <paramref name="seed"/> toward HIGHER columns through the run of
    /// <paramref name="mask"/> it sits in, within one 64-bit word.
    /// </summary>
    /// <remarks>
    /// <b>The sequential half of a flood, done in six steps instead of sixty-four.</b> Filling a run
    /// one column at a time is a chain of dependent operations the width of the word; this is the
    /// standard Kogge-Stone occluded fill, which doubles the reach each step -- 1, 2, 4, 8, 16, 32 --
    /// while narrowing the mask to the runs that are still contiguous at that distance, so six steps
    /// cover every distance a word can hold. A bit only ever moves through mask bits, which is what
    /// makes it a flood and not a smear: a gap in the mask stops it, exactly as an unset candidate
    /// pixel stops absence spreading into the frame.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong KoggeStoneFillUp(ulong seed, ulong mask)
    {
        seed |= mask & (seed << 1);
        mask &= mask << 1;
        seed |= mask & (seed << 2);
        mask &= mask << 2;
        seed |= mask & (seed << 4);
        mask &= mask << 4;
        seed |= mask & (seed << 8);
        mask &= mask << 8;
        seed |= mask & (seed << 16);
        mask &= mask << 16;
        seed |= mask & (seed << 32);
        return seed;
    }

    /// <summary>The mirror of <see cref="KoggeStoneFillUp"/>, toward LOWER columns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong KoggeStoneFillDown(ulong seed, ulong mask)
    {
        seed |= mask & (seed >> 1);
        mask &= mask >> 1;
        seed |= mask & (seed >> 2);
        mask &= mask >> 2;
        seed |= mask & (seed >> 4);
        mask &= mask >> 4;
        seed |= mask & (seed >> 8);
        mask &= mask >> 8;
        seed |= mask & (seed >> 16);
        mask &= mask >> 16;
        seed |= mask & (seed >> 32);
        return seed;
    }

    /// <summary>What one walk of the pixels can say about absence, so nothing has to walk them twice.</summary>
    /// <param name="Absent">Unusable AND reachable from the border: the canvas ring.</param>
    /// <param name="NaN">Which pixels are NaN in any channel, ring included, or null when not asked for.</param>
    /// <param name="AnyNaN">Whether the frame carries a NaN at all.</param>
    private readonly record struct AbsenceScan(BitMatrix Absent, BitMatrix? NaN, bool AnyNaN);

    /// <summary>
    /// Replaces every INTERIOR NaN with the mean of its valid neighbours, in place, and returns how many
    /// pixel-channels were written. The canvas ring is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para><b>A drizzle canvas carries NaN where no drop's footprint reached</b>, and those pixels are
    /// scattered through the interior rather than gathered at the edge: 53 of the 79 masters in one bake,
    /// every <c>BayerDrizzle</c> one, 19 to 324 components and 35 to 1,856 px (issue #250). Since 8.0 the
    /// crop KEEPS them (see <see cref="LargestCoveredRectangle()"/>), which is right for the rectangle
    /// and leaves every consumer downstream holding a NaN it has to have an answer for: the render paints
    /// it black, a statistic has to remember to skip it, a save writes it back out.</para>
    /// <para><b>The ring is not filled, and that separation is the whole reason this is safe.</b> A ring
    /// is genuinely absent -- no frame reached it, and inventing a number there would erase the only
    /// evidence the crop has to work from. A hole is surrounded by data that was measured, so the mean of
    /// its neighbours is an interpolation rather than an invention. Both come out of the same
    /// <see cref="ScanAbsence"/> call the crop uses, so the two can never draw the line in
    /// different places.</para>
    /// <para><b>Per channel, because the absence test is not.</b> A pixel counts as a hole when ANY
    /// channel is NaN, but only the channels that are actually NaN get written; a green plane that has a
    /// number keeps it.</para>
    /// <para>Each pass fills only from neighbours that already have a value, and every value in a pass is
    /// read before any is written, so the result does not depend on the order pixels are visited. A hole
    /// closes from its rim inward at one pixel per pass, so the cap bounds the RADIUS a hole can have;
    /// anything still NaN after it is left alone, which is the honest outcome rather than a fabricated
    /// one. The real distribution needs three passes at most.</para>
    /// <para><b>An in-place mutation, and the name says so</b>, per the rule on <see cref="Image"/> that
    /// the four (now five) deliberate mutators state it. Unlike a rescale this one does not invalidate
    /// anything on the instance: the pixels it writes lie between their own neighbours, so every
    /// statistic the image carries stays as true as it was.</para>
    /// </remarks>
    /// <summary>
    /// This image with its interior holes filled, for a caller that does not OWN the pixels: the same
    /// instance when there is nothing to fill, a filled copy otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>The non-owning half of <see cref="FillInteriorHolesInPlace"/>, added so the headless
    /// render path can obey the rule the viewer already did.</b> `AdoptImageAsync` fills in place
    /// because it is an ownership-transfer factory and says so in its name; `MasterPreviewRenderer`
    /// documents that it does NOT mutate the master it is handed, and the master it renders is often
    /// the very object the FITS writer is about to write. So it needs the answer without the
    /// mutation, and what it must not do is grow a second fill of its own.</para>
    /// <para><b>A frame with no hole pays one classify pass and no copy</b>, which is the common case:
    /// <see cref="ScanAbsence"/> gives up as soon as it has seen no NaN, before the flood. A frame
    /// that HAS holes pays that pass twice, once here and once inside the fill, and that is accepted
    /// rather than plumbed around -- it is a display render, not the stacking hot path.</para>
    /// <para>What it was for: a Great Orion Nebula master carries 1,853 NaN in a 64 x 68 box at the
    /// Trapezium, where the core saturates in the subs and rejection took every sample. Rendered
    /// unfilled they reach the PNG as a blue-and-yellow speck sitting on the brightest part of the
    /// picture. Nobody saw it until the crop stopped cutting that half of the frame away.</para>
    /// </remarks>
    public Image WithInteriorHolesFilled(int maxPasses = 32)
    {
        if (Width <= 0 || Height <= 0 || ChannelCount <= 0 || !ScanAbsence(trackNaN: true).AnyNaN)
        {
            return this;
        }

        var copy = Crop(new PixelRect(0, 0, Width, Height));
        copy.FillInteriorHolesInPlace(maxPasses);
        return copy;
    }

    /// <param name="maxPasses">How far a fill may reach into a hole, in pixels.</param>
    public int FillInteriorHolesInPlace(int maxPasses = 32)
    {
        var width = Width;
        var height = Height;
        var channels = ChannelCount;
        if (width <= 0 || height <= 0 || channels <= 0)
        {
            return 0;
        }

        // ONE walk of the pixel DATA, shared with the crop. The classify pass already reads every channel
        // of every pixel to decide what is unusable, and already knows which of those were NaN, so it
        // hands that set back rather than being asked again: re-deriving it here would double the only
        // expensive part of this, on the document load path, to learn something already computed.
        var scan = ScanAbsence(trackNaN: true);
        if (!scan.AnyNaN || scan.NaN is not { } nan)
        {
            return 0;
        }

        // What is left is a walk of BITS -- no float reads, no per-channel indirection -- and it yields
        // the hole list each pass then iterates instead of the frame. A hole closes from its rim inward
        // at one pixel per pass, so a frame-wide scan per pass per channel would be a dozen passes over
        // nine million pixels to write under two thousand of them.
        // NextSetBit walks WORDS, so an empty stretch of row costs one test per 64 columns rather than
        // 64 tests. A hole census is 0.02 percent of a frame; a per-column loop pays for the other 99.98.
        var absent = scan.Absent;
        var holes = new List<(int Y, int X)>(nan.PopCount());
        for (var y = 0; y < height; y++)
        {
            for (var x = nan.NextSetBit(y, 0); x >= 0; x = nan.NextSetBit(y, x + 1))
            {
                if (!absent[y, x])
                {
                    holes.Add((y, x));
                }
            }
        }

        if (holes.Count == 0)
        {
            return 0;
        }

        var filled = 0;
        var pending = new List<(int Y, int X, float Value)>();

        for (var c = 0; c < channels; c++)
        {
            var plane = GetChannelArray(c);

            for (var pass = 0; pass < maxPasses; pass++)
            {
                pending.Clear();

                foreach (var (y, x) in holes)
                {
                    if (float.IsNaN(plane[y, x]))
                    {
                        var sum = 0f;
                        var n = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var ny = y + dy;
                            if (ny < 0 || ny >= height)
                            {
                                continue;
                            }

                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var nx = x + dx;
                                if ((dx == 0 && dy == 0) || nx < 0 || nx >= width)
                                {
                                    continue;
                                }

                                var v = plane[ny, nx];
                                if (!float.IsNaN(v))
                                {
                                    sum += v;
                                    n++;
                                }
                            }
                        }

                        if (n > 0)
                        {
                            pending.Add((y, x, sum / n));
                        }
                    }
                }

                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var (y, x, value) in pending)
                {
                    plane[y, x] = value;
                }

                filled += pending.Count;
            }
        }

        return filled;
    }

    /// <summary>
    /// The largest axis-aligned rectangle in which every pixel was reached by at least
    /// <paramref name="minFraction"/> of the frames, read off a coverage plane rather than guessed from
    /// the pixels. The exact answer to the question <see cref="LargestCoveredRectangle()"/> only
    /// approximates, and the one that wins wherever a coverage plane exists.
    /// </summary>
    /// <param name="coverage">Per-pixel accumulated weight on the same canvas as this image: TianWen's
    /// drizzle strategies emit exactly this as <c>IntegrationResult.RejectionMap</c> (there is no
    /// kappa-sigma rejection to report, so the field carries the weight), written beside the master as
    /// <c>*.rejection.fits</c>. One channel is broadcast to all; otherwise the channel count must match,
    /// because a Bayer-drizzle canvas covers green twice as often as red and the levels are per
    /// channel.</param>
    /// <param name="minFraction">How much of full coverage a region needs, where full is the median of
    /// the central half of that channel's plane. 0.95 keeps the noise within 1/sqrt(0.95) = 1.026x of the
    /// interior's, which is below anything visible under a stretch.</param>
    /// <param name="blockSize">Side of the block the coverage is averaged over before it is compared, and
    /// therefore the granularity of the answer.</param>
    /// <remarks>
    /// <para><b>The comparison is per BLOCK, not per pixel, and that is not a shortcut.</b> A drizzle
    /// canvas hands neighbouring cells slightly different drop counts, so the per-pixel weight inside a
    /// fully covered interior scatters by about 10% either way: measured on the 10P master, the red
    /// channel's interior p0.1 is 0.847 of its median. A per-pixel threshold at 0.95 therefore rejects
    /// pixels everywhere and the largest rectangle collapses to nothing (207 x 404 of a 4215 x 2884
    /// frame, measured, before this was blocked). Over 16 x 16 blocks the same interior reads p0.1 =
    /// 0.990, because whether a REGION is under-exposed is the actual question -- one cell's drop count
    /// is not.</para>
    /// <para><b>Full coverage is the central MEDIAN, not the maximum.</b> A single cell that took more
    /// drops than its neighbours would otherwise set the level and push the threshold above what the
    /// frame actually achieved, which trims a healthy master.</para>
    /// <para><b>Every channel has to pass.</b> A pixel under-covered in one channel of three is
    /// under-covered: it renders as colour noise, which is worse than luminance noise, and the drizzle
    /// canvas ramps the three channels in at slightly different depths.</para>
    /// <para>Measured on TianWen's own 10P master (4215 x 2884, 135 frames, RGGB drizzle): the coverage
    /// plane puts the under-exposed band at 56 px on the top edge, 56 on the bottom, 20 on the right and
    /// 4 on the left, where the union rectangle of <see cref="LargestCoveredRectangle()"/> keeps all four
    /// -- its top row sits at 8% coverage. The stacking pipeline's own <c>_autocrop</c> is unaffected: it
    /// comes from the geometric intersection of the frame footprints and already lands inside 0.99
    /// coverage on all four edges.</para>
    /// </remarks>
    public PixelRect LargestCoveredRectangle(Image coverage, double minFraction = 0.95, int blockSize = 16)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var width = Width;
        var height = Height;
        var channels = ChannelCount;
        if (width <= 0 || height <= 0 || channels <= 0)
        {
            return PixelRect.Empty;
        }
        if (coverage.Width != width || coverage.Height != height)
        {
            throw new ArgumentException(
                $"Coverage plane is {coverage.Width}x{coverage.Height}, image is {width}x{height}.", nameof(coverage));
        }
        if (coverage.ChannelCount != 1 && coverage.ChannelCount != channels)
        {
            throw new ArgumentException(
                $"Coverage plane has {coverage.ChannelCount} channels, image has {channels}.", nameof(coverage));
        }

        var block = Math.Max(1, blockSize);
        var planes = coverage.ChannelCount;
        var gridWidth = (width + block - 1) / block;
        var gridHeight = (height + block - 1) / block;
        var grids = new float[planes][];
        var thresholds = new float[planes];
        for (var c = 0; c < planes; c++)
        {
            grids[c] = BlockMeans(coverage, c, block, gridWidth, gridHeight);
            thresholds[c] = (float)(minFraction * CentralMedian(grids[c], gridWidth, gridHeight));
        }

        // UNDER-COVERAGE IS ONLY ABSENCE WHERE IT REACHES THE BORDER, the same rule the pixel tier has
        // had since issue #250 and the reason FloodFromBorder is a method. A crop exists to trim the
        // under-exposed RIM; a deficit in the MIDDLE of the frame is a different fact about a real
        // subject, and routing a largest RECTANGLE around one throws the subject away to avoid it.
        //
        // The case that found it: the Great Orion Nebula master (3024 x 3025, 77 frames, RGGB drizzle).
        // The Trapezium saturates in the subs, rejection drops those samples, and the weight plane
        // honestly records the deficit -- accumulated weight falls to 0.79 / 0.93 / 0.81 of the
        // surrounding level with individual pixels at zero, worst in red, which is where an Ha-dominant
        // passband saturates first. That is 35 blocks of 35,910, one 96 x 112 island dead centre, and it
        // took the answer to 1600 x 2960, 51.8% of a canvas whose blocks pass at 97.3%: the left half of
        // the picture, with the nebula sliced off. The master there is not clipped (peak 0.98 against
        // the frame's 1.0296); it is simply 145x the sky in red.
        var underCovered = new BitMatrix(gridHeight, gridWidth);
        for (var gy = 0; gy < gridHeight; gy++)
        {
            var row = underCovered.RowWords(gy);
            for (var gx = 0; gx < gridWidth; gx++)
            {
                for (var c = 0; c < planes; c++)
                {
                    // Negated >= so a NaN block counts as uncovered rather than passing.
                    if (!(grids[c][(gy * gridWidth) + gx] >= thresholds[c]))
                    {
                        row[gx >> 6] |= 1ul << (gx & 63);
                        break;
                    }
                }
            }
        }

        var absentBlocks = new BitMatrix(gridHeight, gridWidth);
        FloodFromBorder(underCovered, absentBlocks, gridWidth, gridHeight);

        return LargestRectangle(width, height, (int y, Span<bool> covered) =>
        {
            var gridRow = y / block;
            for (var x = 0; x < width; x++)
            {
                covered[x] = !absentBlocks[gridRow, x / block];
            }
        });
    }

    /// <summary>
    /// Block means of one plane, NaN for a block holding any non-finite weight -- an unknown weight is
    /// not a full one, and the maximal-rectangle scan then treats the block as uncovered.
    /// </summary>
    private static float[] BlockMeans(Image coverage, int channel, int block, int gridWidth, int gridHeight)
    {
        var width = coverage.Width;
        var height = coverage.Height;
        var plane = coverage.GetChannelSpan(channel);
        var grid = new float[gridWidth * gridHeight];

        for (var gy = 0; gy < gridHeight; gy++)
        {
            var y1 = Math.Min(height, (gy + 1) * block);
            for (var gx = 0; gx < gridWidth; gx++)
            {
                var x1 = Math.Min(width, (gx + 1) * block);
                var sum = 0.0;
                var count = 0;
                var bad = false;
                for (var y = gy * block; y < y1 && !bad; y++)
                {
                    var row = y * width;
                    for (var x = gx * block; x < x1; x++)
                    {
                        var v = plane[row + x];
                        if (!float.IsFinite(v))
                        {
                            bad = true;
                            break;
                        }
                        sum += v;
                        count++;
                    }
                }
                grid[gy * gridWidth + gx] = bad || count == 0 ? float.NaN : (float)(sum / count);
            }
        }

        return grid;
    }

    /// <summary>
    /// <see cref="LargestCoveredRectangle()"/> with each edge's under-exposed band walked off as well --
    /// the best answer available for a frame that carries no coverage plane. See
    /// <see cref="CoverageEdgeWalk"/> for what the walk can and cannot see.
    /// </summary>
    public PixelRect SettledCoverageRectangle(CoverageEdgeWalkOptions? options = null)
    {
        var start = LargestCoveredRectangle();
        return start.Width > 0 && start.Height > 0 ? CoverageEdgeWalk.Trim(this, start, options) : start;
    }

    /// <summary>The median of the central half of a block grid: what "full" means for a coverage map.</summary>
    private static double CentralMedian(float[] grid, int gridWidth, int gridHeight)
    {
        var x0 = gridWidth / 4;
        var x1 = Math.Max(x0 + 1, gridWidth - x0);
        var y0 = gridHeight / 4;
        var y1 = Math.Max(y0 + 1, gridHeight - y0);

        var samples = new float[(x1 - x0) * (y1 - y0)];
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * gridWidth;
            for (var x = x0; x < x1; x++)
            {
                var v = grid[row + x];
                if (float.IsFinite(v))
                {
                    samples[count++] = v;
                }
            }
        }
        if (count == 0)
        {
            return 0.0;
        }
        Array.Sort(samples, 0, count);
        return samples[count / 2];
    }

    /// <summary>Fills <paramref name="covered"/> with row <paramref name="y"/>'s per-column verdict.</summary>
    private delegate void CoveredRowProbe(int y, Span<bool> covered);

    /// <summary>
    /// The largest-rectangle-under-a-histogram scan, shared by every definition of "covered" -- the
    /// definition is the caller's and only reaches here as a row of booleans.
    /// </summary>
    private static PixelRect LargestRectangle(int width, int height, CoveredRowProbe probe)
    {
        var heights = new int[width];
        var stack = new int[width];
        var covered = new bool[width];
        var best = PixelRect.Empty;
        var bestArea = 0L;

        for (var y = 0; y < height; y++)
        {
            probe(y, covered);

            for (var x = 0; x < width; x++)
            {
                heights[x] = covered[x] ? heights[x] + 1 : 0;
            }

            // Monotonic stack of indices with strictly increasing heights. The sentinel pass at x == width
            // drains it, so a run reaching the right edge is measured like any other, and the left bound
            // comes from the stack rather than from mutating heights (which has to survive to the next row).
            var top = 0;
            for (var x = 0; x <= width; x++)
            {
                var h = x < width ? heights[x] : 0;
                while (top > 0 && heights[stack[top - 1]] >= h)
                {
                    var barHeight = heights[stack[--top]];
                    var left = top > 0 ? stack[top - 1] + 1 : 0;
                    var barWidth = x - left;
                    var area = (long)barWidth * barHeight;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = new PixelRect(left, y - barHeight + 1, barWidth, barHeight);
                    }
                }

                if (x < width)
                {
                    stack[top++] = x;
                }
            }
        }

        return best;
    }
}
