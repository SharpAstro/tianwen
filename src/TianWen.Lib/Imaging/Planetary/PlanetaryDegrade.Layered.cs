using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

public static partial class PlanetaryDegrade
{
    /// <summary>The side of the patch about a field point its true quality is read over, px (an alignment point's).</summary>
    public const int FieldPatchPx = 16;

    // The frames seen through a layer at an altitude (docs/plans/planetary-restoration.md, R4 per-point, #1071). Each field point of the
    // grid looks through the layer at its own footprint; its PSF's tilt is the warp there and the frame moves by the points' mean tilt;
    // its tilt-removed blur is applied to the object about it, the points' images blended by tent weights (Nagy and O'Leary 1998), so the
    // blur varies smoothly over the disk. Blending PSFs that kept their tilts would put two peaks where one moved.
    private static ImmutableArray<SyntheticFrame> MakeLayered(PlanetMap map, CatalogIndex planet, ImmutableArray<DateTimeOffset> times, FieldGrid grid,
        int windowX, int windowY, DiskPlacement finePlacement, double arcsecPerPixel, ImmutableArray<double> shiftX, ImmutableArray<double> shiftY,
        ImmutableArray<double> brightness, int width, int height, DegradeOptions options, Action<int, ushort[]> write, IProgress<int>? progress,
        Action<int, SyntheticWarp>? warps, Action<int, SyntheticFieldFrame>? field, CancellationToken cancellationToken)
    {
        var (fine, os) = (grid.Fine, grid.Os);
        var n = times.Length;
        var diffraction = Transfer(DiffractionPsf(options, arcsecPerPixel));
        var bandOf = BandOfBins(os);
        var span = NextPowerOfTwo((2 * grid.Step) + PsfGrid);
        var start = times[0];
        LayeredEpoch EpochAt(double when)
        {
            var instant = start + TimeSpan.FromSeconds(when + (options.RenderEverySeconds / 2));
            return new LayeredEpoch(ObjectPlane(map, PhysicalEphemeris.Compute(planet, instant), finePlacement, fine, options, options.MoonsAt(planet, instant)), grid, diffraction, bandOf, span);
        }
        var epoch = EpochAt(0);
        var epochTime = 0.0;

        // A field point's footprint on the layer: the altitude times its angle from the disk's centre. The layer's screen holds the
        // footprints of the points the object lights, and two of their steps more.
        var metresPerFine = options.HighAltitudeM * arcsecPerPixel / os / ShortExposurePsf.ArcsecPerRadian;
        double reach = 0;
        foreach (var k in epoch.Lit)
        {
            var (dx, dy) = grid.Offset(k);
            reach = Math.Max(reach, Math.Sqrt((dx * dx) + (dy * dy)));
        }
        var seeing = new SeeingPsfSequence(options, arcsecPerPixel, highReachM: (reach + (2 * grid.Step)) * metresPerFine);
        var diffractionPeak = seeing.DiffractionPeak;
        var scatter = options.ScatterFraction > 0 ? ScatterSpectrum(fine, options.ScatterCoreArcsec / (arcsecPerPixel / os)) : null;
        var kept = scatter is null ? 1 : 1 - options.ScatterFraction;
        var scratches = new ConcurrentBag<NodeScratch>();

        var truths = new SyntheticFrame[n];
        var tilts = new (double X, double Y)[grid.Count];
        var strehls = new double[grid.Count];
        var gains = new double[grid.Recorded.Length * SyntheticFieldFile.Bands];
        var plane = new double[fine * fine];
        var spectrum = new Complex[fine * fine];
        var (rampX, rampY) = (new Complex[fine], new Complex[fine]);
        for (var t = 0; t < n; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The object, rendered afresh when the planet has turned long enough.
            var when = Math.Floor((times[t] - start).TotalSeconds / options.RenderEverySeconds) * options.RenderEverySeconds;
            if (when != epochTime)
            {
                epoch = EpochAt(when);
                epochTime = when;
            }
            if (t > 0)
            {
                seeing.Step((times[t] - times[t - 1]).TotalSeconds);
            }
            seeing.Freeze();

            // Every point that sees any of the object, in four interleaved classes whose tents never overlap, so a class is drawn in parallel.
            Array.Clear(plane);
            var current = epoch;
            foreach (var nodes in current.Classes)
            {
                Parallel.ForEach(nodes, new ParallelOptions { CancellationToken = cancellationToken },
                    () => scratches.TryTake(out var s) ? s : new NodeScratch(span),
                    (k, _, scratch) =>
                    {
                        var (dx, dy) = grid.Offset(k);
                        seeing.ExposureAt(dx * metresPerFine, dy * metresPerFine, scratch.Psf, scratch.Exposure);
                        var (cx, cy) = Centroid(scratch.Psf);
                        tilts[k] = (cx / os, cy / os);
                        strehls[k] = Max(scratch.Psf) / diffractionPeak;
                        BlurAbout(current.PatchSpectrum(k), plane, grid, k, cx, cy, span, scratch);
                        if (grid.RecordIndex(k) is var r and >= 0)
                        {
                            current.Gains(r, scratch.Psf, cx, cy, scratch, gains.AsSpan(r * SyntheticFieldFile.Bands, SyntheticFieldFile.Bands));
                        }
                        return scratch;
                    },
                    scratches.Add);
            }

            // The frame moves by its points' tilt, weighted by the light each carries; what is left at each point is the warp there.
            double weight = 0, meanX = 0, meanY = 0, strehl = 0;
            foreach (var k in current.Lit)
            {
                var w = current.Flux[k];
                weight += w;
                meanX += w * tilts[k].X;
                meanY += w * tilts[k].Y;
                strehl += w * strehls[k];
            }
            (meanX, meanY, strehl) = weight > 0 ? (meanX / weight, meanY / weight, strehl / weight) : (0, 0, 0);
            var (moveX, moveY) = (shiftX[t] + meanX, shiftY[t] + meanY);
            var (ix, iy) = ((int)Math.Round(moveX), (int)Math.Round(moveY));
            var (fx0, fy0) = ((moveX - ix) * os, (moveY - iy) * os);

            // The blurred object, with the telescope's scatter beside it, moved by the fraction of its shift.
            for (var i = 0; i < plane.Length; i++)
            {
                spectrum[i] = plane[i];
            }
            Fft2D.Forward(spectrum, fine, fine);
            Ramp(rampX, -fx0);
            Ramp(rampY, -fy0);
            for (var ky = 0; ky < fine; ky++)
            {
                for (var kx = 0; kx < fine; kx++)
                {
                    var i = (ky * fine) + kx;
                    var value = scatter is null ? spectrum[i] : (kept * spectrum[i]) + (options.ScatterFraction * scatter[i] * current.Spectrum[i]);
                    spectrum[i] = value * rampY[ky] * rampX[kx];
                }
            }
            Fft2D.Inverse(spectrum, fine, fine);

            var warp = grid.Warp(tilts, current.IsLit, meanX, meanY);
            var frame = Readout(spectrum, fine, os, warp, windowX, windowY, ix, iy, brightness.IsDefaultOrEmpty ? 1 : brightness[t], width, height, options,
                new Random(unchecked((options.Seed * 1_000_003) + t)));
            write(t, frame);
            truths[t] = new SyntheticFrame(moveX, moveY, strehl);
            warps?.Invoke(t, new SyntheticWarp(windowX + ix, windowY + iy, warp));
            if (field is not null)
            {
                var count = grid.Recorded.Length;
                var (tiltX, tiltY, pointStrehl, pointGains) = (new float[count], new float[count], new float[count], new float[count * SyntheticFieldFile.Bands]);
                for (var r = 0; r < count; r++)
                {
                    var k = grid.Recorded[r];
                    tiltX[r] = (float)(tilts[k].X - meanX);
                    tiltY[r] = (float)(tilts[k].Y - meanY);
                    pointStrehl[r] = (float)strehls[k];
                }
                for (var i = 0; i < pointGains.Length; i++)
                {
                    pointGains[i] = (float)gains[i];
                }
                field(t, new SyntheticFieldFrame(grid.Points, windowX + ix, windowY + iy, tiltX, tiltY, pointStrehl, pointGains));
            }
            if ((t + 1) % Block == 0 || t == n - 1)
            {
                progress?.Report(t + 1);
            }
        }
        return [.. truths];
    }

    // Point k's tilt-removed PSF applied to the object about it, its image weighted by the point's tent and added to `into`: the object's
    // patch of `span` samples centred on the point (its spectrum, `patchSpectrum`), through the PSF moved back by its centroid (`cx`, `cy`
    // fine samples), on the tent's open support, where the circular transform's wrap cannot reach. The PSF fills half the kernel's rows,
    // so only those are transformed; only the tent's rows are transformed back.
    private static void BlurAbout(Complex[] patchSpectrum, double[] into, FieldGrid grid, int k, double cx, double cy, int span, NodeScratch scratch)
    {
        var fine = grid.Fine;
        var step = grid.Step;
        var (nx, ny) = grid.Position(k);
        var (patch, kernel) = (scratch.Patch, scratch.Kernel);
        Array.Clear(kernel);
        for (var y = 0; y < PsfGrid; y++)
        {
            var ky = (y - (PsfGrid / 2) + span) % span;
            var row = kernel.AsSpan(ky * span, span);
            for (var x = 0; x < PsfGrid; x++)
            {
                row[(x - (PsfGrid / 2) + span) % span] = scratch.Psf[(y * PsfGrid) + x];
            }
            ComplexFft.Forward(row);
        }
        Columns(kernel, span, scratch.Column, inverse: false);
        Ramp(scratch.RampX, cx);
        Ramp(scratch.RampY, cy);
        for (var ky = 0; ky < span; ky++)
        {
            for (var kx = 0; kx < span; kx++)
            {
                var i = (ky * span) + kx;
                patch[i] = patchSpectrum[i] * kernel[i] * scratch.RampY[ky] * scratch.RampX[kx];
            }
        }
        Columns(patch, span, scratch.Column, inverse: true);
        for (var dy = 1 - step; dy < step; dy++)
        {
            ComplexFft.Inverse(patch.AsSpan(((span / 2) + dy) * span, span));
        }
        for (var dy = 1 - step; dy < step; dy++)
        {
            var y = ny + dy;
            if (y < 0 || y >= fine)
            {
                continue;
            }
            var wy = 1 - (Math.Abs(dy) / (double)step);
            for (var dx = 1 - step; dx < step; dx++)
            {
                var x = nx + dx;
                if (x < 0 || x >= fine)
                {
                    continue;
                }
                into[(y * fine) + x] += wy * (1 - (Math.Abs(dx) / (double)step)) * patch[(((span / 2) + dy) * span) + (span / 2) + dx].Real;
            }
        }
    }

    // Every column of a square `span` transform, gathered into `column` and transformed.
    private static void Columns(Complex[] data, int span, Complex[] column, bool inverse)
    {
        for (var x = 0; x < span; x++)
        {
            for (var y = 0; y < span; y++)
            {
                column[y] = data[(y * span) + x];
            }
            if (inverse)
            {
                ComplexFft.Inverse(column);
            }
            else
            {
                ComplexFft.Forward(column);
            }
            for (var y = 0; y < span; y++)
            {
                data[(y * span) + x] = column[y];
            }
        }
    }

    // A shift of `by` samples along one axis of a transform as long as `ramp`: e^(2 pi i f by) at each of its signed frequencies.
    private static void Ramp(Complex[] ramp, double by)
    {
        var n = ramp.Length;
        for (var k = 0; k < n; k++)
        {
            ramp[k] = Complex.FromPolarCoordinates(1, 2 * Math.PI * (k < n / 2 ? k : k - n) / n * by);
        }
    }

    // A PSF on the PSF grid, centred on sample PsfGrid / 2, as its transfer function with that centre at the origin.
    private static Complex[] Transfer(double[] psf)
    {
        var transfer = new Complex[PsfGrid * PsfGrid];
        Wrapped(psf, transfer);
        Fft2D.Forward(transfer, PsfGrid, PsfGrid);
        return transfer;
    }

    private static void Wrapped(double[] psf, Complex[] into)
    {
        for (var y = 0; y < PsfGrid; y++)
        {
            var wy = (y - (PsfGrid / 2) + PsfGrid) % PsfGrid;
            for (var x = 0; x < PsfGrid; x++)
            {
                into[(wy * PsfGrid) + ((x - (PsfGrid / 2) + PsfGrid) % PsfGrid)] = psf[(y * PsfGrid) + x];
            }
        }
    }

    // Each bin of the PSF grid's transform, the a trous band (1 to 4) its frequency falls in, 0 for none: band j from 2^-(j+1) to 2^-j
    // cycles a pixel, the fine grid's frequencies `os` times a pixel's.
    private static int[] BandOfBins(int os)
    {
        var bands = new int[PsfGrid * PsfGrid];
        for (var ky = 0; ky < PsfGrid; ky++)
        {
            var fy = (ky < PsfGrid / 2 ? ky : ky - PsfGrid) / (double)PsfGrid;
            for (var kx = 0; kx < PsfGrid; kx++)
            {
                var fx = (kx < PsfGrid / 2 ? kx : kx - PsfGrid) / (double)PsfGrid;
                var f = Math.Sqrt((fx * fx) + (fy * fy)) * os;
                for (var j = 1; j <= SyntheticFieldFile.Bands; j++)
                {
                    if (f >= Math.Pow(2, -(j + 1)) && f < Math.Pow(2, -j))
                    {
                        bands[(ky * PsfGrid) + kx] = j;
                        break;
                    }
                }
            }
        }
        return bands;
    }

    // What one thread needs to make a field point's PSF and blur the object about it.
    private sealed class NodeScratch(int span)
    {
        public double[] Psf { get; } = new double[PsfGrid * PsfGrid];

        public SeeingPsfSequence.ExposureScratch Exposure { get; } = new SeeingPsfSequence.ExposureScratch();

        public Complex[] Patch { get; } = new Complex[span * span];

        public Complex[] Kernel { get; } = new Complex[span * span];

        public Complex[] Small { get; } = new Complex[PsfGrid * PsfGrid];

        public Complex[] Column { get; } = new Complex[span];

        public Complex[] RampX { get; } = new Complex[span];

        public Complex[] RampY { get; } = new Complex[span];

        public Complex[] SmallRampX { get; } = new Complex[PsfGrid];

        public Complex[] SmallRampY { get; } = new Complex[PsfGrid];
    }

    /// <summary>
    /// The field points of a layered capture on its fine grid: every <c>FieldGridPx</c> pixels from the disk's centre, rounded to a fine
    /// sample, over the whole grid; those within a radius and a half patch of the centre are recorded (<see cref="SyntheticFieldFrame"/>).
    /// </summary>
    internal sealed class FieldGrid
    {
        private readonly int _iMin;
        private readonly int _jMin;
        private readonly int _columns;
        private readonly int _rows;
        private readonly int[] _recordIndex;

        public FieldGrid(int fine, int os, int gridPx, DiskPlacement finePlacement, double radiusPx)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(gridPx, 1);
            (Fine, Os, Step) = (fine, os, gridPx * os);
            (CentreX, CentreY) = ((int)Math.Round(finePlacement.CenterX), (int)Math.Round(finePlacement.CenterY));
            // Every point whose tent reaches the grid: centre + i step within a step of it.
            _iMin = (int)Math.Ceiling((-Step - CentreX) / (double)Step);
            var iMax = (int)Math.Floor((fine - 1 + Step - CentreX) / (double)Step);
            _jMin = (int)Math.Ceiling((-Step - CentreY) / (double)Step);
            var jMax = (int)Math.Floor((fine - 1 + Step - CentreY) / (double)Step);
            (_columns, _rows) = (iMax - _iMin + 1, jMax - _jMin + 1);
            Count = _columns * _rows;
            _recordIndex = new int[Count];
            var recorded = ImmutableArray.CreateBuilder<int>();
            var points = ImmutableArray.CreateBuilder<(double X, double Y)>();
            var limit = (radiusPx + (FieldPatchPx / 2.0)) * os;
            for (var k = 0; k < Count; k++)
            {
                var (dx, dy) = Offset(k);
                if ((dx * dx) + (dy * dy) <= limit * limit)
                {
                    _recordIndex[k] = recorded.Count;
                    recorded.Add(k);
                    var (x, y) = Position(k);
                    points.Add((((x + 0.5) / os) - 0.5, ((y + 0.5) / os) - 0.5));
                }
                else
                {
                    _recordIndex[k] = -1;
                }
            }
            (Recorded, Points) = (recorded.ToImmutable(), points.ToImmutable());
        }

        public int Fine { get; }

        public int Os { get; }

        /// <summary>The points' spacing, fine samples.</summary>
        public int Step { get; }

        public int CentreX { get; }

        public int CentreY { get; }

        public int Count { get; }

        /// <summary>The recorded points' indices, and where each is in the rendered window's pixels.</summary>
        public ImmutableArray<int> Recorded { get; }

        public ImmutableArray<(double X, double Y)> Points { get; }

        /// <summary>Point k's place among the recorded ones, -1 when it is not recorded.</summary>
        public int RecordIndex(int k) => _recordIndex[k];

        /// <summary>Point k on the fine grid.</summary>
        public (int X, int Y) Position(int k) => (CentreX + ((_iMin + (k % _columns)) * Step), CentreY + ((_jMin + (k / _columns)) * Step));

        /// <summary>Point k from the centre, fine samples.</summary>
        public (double X, double Y) Offset(int k) => ((_iMin + (k % _columns)) * Step, (_jMin + (k / _columns)) * Step);

        /// <summary>Which of the four interleaved classes point k is in: no two points of one class share any of their tents.</summary>
        public int Class(int k) => ((_iMin + (k % _columns)) & 1) | (((_jMin + (k / _columns)) & 1) << 1);

        /// <summary>
        /// The warp: each lit point's tilt over the frame's mean, read between the points bilinearly at the nodes of a
        /// <see cref="SyntheticWarpField"/> over the window, nothing where no lit point is.
        /// </summary>
        public SyntheticWarpField Warp((double X, double Y)[] tilts, bool[] lit, double meanX, double meanY)
        {
            var windowPx = Fine / Os;
            var nodes = (windowPx / SyntheticWarpField.GridStep) + 2;
            var (x, y) = (new float[nodes * nodes], new float[nodes * nodes]);
            for (var gy = 0; gy < nodes; gy++)
            {
                for (var gx = 0; gx < nodes; gx++)
                {
                    // The node in fine samples, then in steps from the first point.
                    var fx = (((gx * SyntheticWarpField.GridStep) + 0.5) * Os) - 0.5;
                    var fy = (((gy * SyntheticWarpField.GridStep) + 0.5) * Os) - 0.5;
                    var u = ((fx - CentreX) / Step) - _iMin;
                    var v = ((fy - CentreY) / Step) - _jMin;
                    var (i0, j0) = ((int)Math.Floor(u), (int)Math.Floor(v));
                    var (tu, tv) = (u - i0, v - j0);
                    double sx = 0, sy = 0;
                    for (var b = 0; b < 2; b++)
                    {
                        for (var a = 0; a < 2; a++)
                        {
                            var (i, j) = (i0 + a, j0 + b);
                            if (i < 0 || j < 0 || i >= _columns || j >= _rows)
                            {
                                continue;
                            }
                            var k = (j * _columns) + i;
                            if (!lit[k])
                            {
                                continue;
                            }
                            var w = (a == 0 ? 1 - tu : tu) * (b == 0 ? 1 - tv : tv);
                            sx += w * (tilts[k].X - meanX);
                            sy += w * (tilts[k].Y - meanY);
                        }
                    }
                    (x[(gy * nodes) + gx], y[(gy * nodes) + gx]) = ((float)sx, (float)sy);
                }
            }
            return new SyntheticWarpField(nodes, x, y);
        }
    }

    // What every frame of one render shares: the object, its spectrum, which points see any of it (in their classes), the light each
    // carries, and each recorded point's truth power in its band bins.
    private sealed class LayeredEpoch
    {
        private readonly Complex[] _diffraction;
        private readonly int[] _bandOf;
        // Each recorded point's truth power per bin (the patch's, through the diffraction limit's transfer squared), and its sum per band.
        private readonly double[][] _power;
        private readonly double[] _denominator;
        private readonly Complex[]?[] _patches;

        public LayeredEpoch(double[] objectPlane, FieldGrid grid, Complex[] diffraction, int[] bandOf, int span)
        {
            (Plane, _diffraction, _bandOf) = (objectPlane, diffraction, bandOf);
            var fine = grid.Fine;
            Spectrum = new Complex[fine * fine];
            for (var i = 0; i < objectPlane.Length; i++)
            {
                Spectrum[i] = objectPlane[i];
            }
            Fft2D.Forward(Spectrum, fine, fine);

            // Summed-area tables of where the object is and of its light.
            var (seen, light) = (new int[(fine + 1) * (fine + 1)], new double[(fine + 1) * (fine + 1)]);
            for (var y = 0; y < fine; y++)
            {
                for (var x = 0; x < fine; x++)
                {
                    var v = objectPlane[(y * fine) + x];
                    var i = ((y + 1) * (fine + 1)) + x + 1;
                    seen[i] = (v != 0 ? 1 : 0) + seen[i - 1] + seen[i - fine - 1] - seen[i - fine - 2];
                    light[i] = v + light[i - 1] + light[i - fine - 1] - light[i - fine - 2];
                }
            }
            int Box(int[] table, int x0, int y0, int x1, int y1)
            {
                (x0, y0, x1, y1) = (Math.Clamp(x0, 0, fine), Math.Clamp(y0, 0, fine), Math.Clamp(x1, 0, fine), Math.Clamp(y1, 0, fine));
                return x1 <= x0 || y1 <= y0 ? 0 : table[(y1 * (fine + 1)) + x1] - table[(y0 * (fine + 1)) + x1] - table[(y1 * (fine + 1)) + x0] + table[(y0 * (fine + 1)) + x0];
            }
            double Sum(double[] table, int x0, int y0, int x1, int y1)
            {
                (x0, y0, x1, y1) = (Math.Clamp(x0, 0, fine), Math.Clamp(y0, 0, fine), Math.Clamp(x1, 0, fine), Math.Clamp(y1, 0, fine));
                return x1 <= x0 || y1 <= y0 ? 0 : table[(y1 * (fine + 1)) + x1] - table[(y0 * (fine + 1)) + x1] - table[(y1 * (fine + 1)) + x0] + table[(y0 * (fine + 1)) + x0];
            }

            // A point is lit when its tent, widened by the PSF's reach, holds any of the object; every recorded point is drawn.
            var step = grid.Step;
            var reach = step + (PsfGrid / 2);
            IsLit = new bool[grid.Count];
            Flux = new double[grid.Count];
            var classes = new ImmutableArray<int>.Builder[] { ImmutableArray.CreateBuilder<int>(), ImmutableArray.CreateBuilder<int>(), ImmutableArray.CreateBuilder<int>(), ImmutableArray.CreateBuilder<int>() };
            var lit = ImmutableArray.CreateBuilder<int>();
            for (var k = 0; k < grid.Count; k++)
            {
                var (nx, ny) = grid.Position(k);
                if (grid.RecordIndex(k) < 0 && Box(seen, nx - reach, ny - reach, nx + reach + 1, ny + reach + 1) == 0)
                {
                    continue;
                }
                IsLit[k] = true;
                Flux[k] = Sum(light, nx - step + 1, ny - step + 1, nx + step, ny + step);
                classes[grid.Class(k)].Add(k);
                lit.Add(k);
            }
            Classes = [.. classes.Select(c => c.ToImmutable())];
            Lit = lit.ToImmutable();

            // Each lit point's patch of the object, `span` samples about it, transformed once for every frame of the render.
            _patches = new Complex[grid.Count][];
            Parallel.ForEach(Lit, k =>
            {
                var (nx, ny) = grid.Position(k);
                var patch = new Complex[span * span];
                var (ox, oy) = (nx - (span / 2), ny - (span / 2));
                for (var y = 0; y < span; y++)
                {
                    var fy = oy + y;
                    for (var x = 0; x < span; x++)
                    {
                        var fx = ox + x;
                        patch[(y * span) + x] = fx >= 0 && fy >= 0 && fx < fine && fy < fine ? objectPlane[(fy * fine) + fx] : 0;
                    }
                }
                Fft2D.Forward(patch, span, span);
                _patches[k] = patch;
            });

            // Each recorded point's truth: the object's patch about it under a Hann window, its power through the diffraction limit.
            var patch = FieldPatchPx * grid.Os;
            _power = new double[grid.Recorded.Length][];
            _denominator = new double[grid.Recorded.Length * SyntheticFieldFile.Bands];
            var bins = new Complex[PsfGrid * PsfGrid];
            for (var r = 0; r < grid.Recorded.Length; r++)
            {
                Array.Clear(bins);
                var (nx, ny) = grid.Position(grid.Recorded[r]);
                for (var y = 0; y < patch; y++)
                {
                    var fy = ny - (patch / 2) + y;
                    var hy = Math.Sin(Math.PI * (y + 0.5) / patch);
                    for (var x = 0; x < patch; x++)
                    {
                        var fx = nx - (patch / 2) + x;
                        var hx = Math.Sin(Math.PI * (x + 0.5) / patch);
                        bins[(y * PsfGrid) + x] = fx >= 0 && fy >= 0 && fx < fine && fy < fine ? objectPlane[(fy * fine) + fx] * hx * hx * hy * hy : 0;
                    }
                }
                Fft2D.Forward(bins, PsfGrid, PsfGrid);
                var power = _power[r] = new double[PsfGrid * PsfGrid];
                for (var i = 0; i < power.Length; i++)
                {
                    power[i] = (bins[i].Real * bins[i].Real) + (bins[i].Imaginary * bins[i].Imaginary);
                    if (bandOf[i] > 0)
                    {
                        var d = diffraction[i];
                        _denominator[(r * SyntheticFieldFile.Bands) + bandOf[i] - 1] += power[i] * ((d.Real * d.Real) + (d.Imaginary * d.Imaginary));
                    }
                }
            }
        }

        public double[] Plane { get; }

        /// <summary>Lit point k's patch of the object, transformed.</summary>
        public Complex[] PatchSpectrum(int k) => _patches[k] ?? throw new InvalidOperationException($"Point {k} is not lit.");

        public Complex[] Spectrum { get; }

        public ImmutableArray<ImmutableArray<int>> Classes { get; }

        public ImmutableArray<int> Lit { get; }

        public bool[] IsLit { get; }

        public double[] Flux { get; }

        /// <summary>
        /// Recorded point r's true quality in each band from its PSF (centroid `cx`, `cy` fine samples, taken out): the transfer over the
        /// diffraction limit's, weighted by the truth's power in the band, into <paramref name="into"/>.
        /// </summary>
        public void Gains(int r, double[] psf, double cx, double cy, NodeScratch scratch, Span<double> into)
        {
            var small = scratch.Small;
            Wrapped(psf, small);
            Fft2D.Forward(small, PsfGrid, PsfGrid);
            Ramp(scratch.SmallRampX, cx);
            Ramp(scratch.SmallRampY, cy);
            into.Clear();
            var power = _power[r];
            for (var ky = 0; ky < PsfGrid; ky++)
            {
                for (var kx = 0; kx < PsfGrid; kx++)
                {
                    var i = (ky * PsfGrid) + kx;
                    if (_bandOf[i] == 0)
                    {
                        continue;
                    }
                    var h = small[i] * scratch.SmallRampY[ky] * scratch.SmallRampX[kx];
                    into[_bandOf[i] - 1] += power[i] * ((h.Real * _diffraction[i].Real) + (h.Imaginary * _diffraction[i].Imaginary));
                }
            }
            for (var b = 0; b < into.Length; b++)
            {
                var d = _denominator[(r * SyntheticFieldFile.Bands) + b];
                into[b] = d > 0 ? into[b] / d : double.NaN;
            }
        }
    }
}
