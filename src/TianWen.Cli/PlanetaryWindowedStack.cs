using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// A capture stacked as R7 part 4 and R8 stack it (the best share by the gradient, global, plain, Lanczos-3), with its two halves when
/// asked, every plane registered onto one disk (the truth's on a twin, the stack's own otherwise), normalised to a sky of zero and a
/// disk of one and cut to a window about it, beside the limb fit, its kernel (b') over the pupil's diffraction, and the limb fit's
/// sharp model in the same window. The one preparation <c>planetary-gains</c> and <c>planetary-dering</c> share.
/// </summary>
internal sealed class PlanetaryWindowedStack : IDisposable
{
    private PlanetaryWindowedStack()
    {
    }

    public required PlanetaryStackResult Result { get; init; }
    public PlanetaryStackResult? HalfAResult { get; init; }
    public PlanetaryStackResult? HalfBResult { get; init; }
    public required int Size { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }
    public required LimbFitOptions LimbOptions { get; init; }
    public required LimbFit Fit { get; init; }
    public required LimbKernel Wide { get; init; }
    /// <summary>The stack's own disk, before it was moved.</summary>
    public required MetricDisk Own { get; init; }
    /// <summary>The disk every plane was moved onto, in the full frame.</summary>
    public required MetricDisk Target { get; init; }
    /// <summary>The disk in the window.</summary>
    public required MetricDisk Disk { get; init; }
    public required float[] Stack { get; init; }
    public float[]? HalfA { get; init; }
    public float[]? HalfB { get; init; }
    public float[]? Truth { get; init; }
    /// <summary>The stack over the whole frame, normalised and registered as <see cref="Stack"/> is: where a moon beyond the window is read.</summary>
    public required float[] FullStack { get; init; }
    public float[]? FullHalfA { get; init; }
    public float[]? FullHalfB { get; init; }
    /// <summary>The first and the last frame's time: the span a moon drifted over.</summary>
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    /// <summary>The limb fit's sharp model, moved as the stack was, in the window.</summary>
    public required float[] SharpDisk { get; init; }
    public required RadialTransfer Diffraction { get; init; }
    public required double ArcsecPerPixel { get; init; }
    /// <summary>The planet's aspect at the capture's middle.</summary>
    public required PlanetAspect Aspect { get; init; }
    /// <summary>The capture's middle.</summary>
    public required DateTimeOffset When { get; init; }

    /// <summary>(b'): the limb fit's kernel over the pupil's diffraction, kept at most one and zero past the cutoff.</summary>
    public Func<double, double> Measured => f => Diffraction.At(f) is var d && d > 0.02 ? Math.Clamp(Wide.TransferAt(f) / d, 0, 1) : 0;

    /// <summary>The sharp model through the pupil's diffraction alone: the truth's disk.</summary>
    public float[] DiskTarget => _diskTarget ??= PlanetaryInverse.Apply(SharpDisk, Size, Size, Diffraction.At);

    private float[]? _diskTarget;

    /// <summary>The pupil's cutoff, D / lambda, in cycles a pixel.</summary>
    public required double Cutoff { get; init; }

    /// <summary>
    /// (b) <paramref name="plane"/>'s edge across the limb against the sharp model through the diffraction (R8 follow-up 3,
    /// <see cref="PlanetaryFinestBand.Edge"/>), each flattened by its own zonal brightness at the latitude its limb point lies at; along
    /// <paramref name="sectorDeg"/> and its opposite only, when given.
    /// </summary>
    public EdgeProfile LimbEdge(float[] plane, double? sectorDeg = null)
    {
        var projection = new PlanetaryProjection(Aspect, new DiskPlacement(Disk.X, Disk.Y, Fit.EquatorialRadius, Fit.NorthAngleDeg));
        return PlanetaryFinestBand.Edge(plane, DiskTarget, Size, Size, Disk, Fit.SunSide, Flatten(plane, projection), Flatten(DiskTarget, projection), sectorDeg);
    }

    // Each limb point's brightness relative to the plane's mean, by the zonal brightness at its latitude.
    private Func<double, double, double> Flatten(float[] plane, PlanetaryProjection projection)
    {
        var zonal = PlanetaryBelts.FromImage(plane, Size, Size, projection, Aspect.CentralMeridianIII, Fit.LimbDarkening);
        var mean = zonal.Albedo.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        return (x, y) => projection.TrySurface(x, y, out var latitude, out _, out _, out _) ? zonal.At(latitude) / mean : double.NaN;
    }

    /// <summary>A full-frame plane cut to the window.</summary>
    public float[] Window(float[] plane) => PlanetaryInversesSubCommand.Crop(plane, Width, Height, OriginX, OriginY, Size);

    /// <summary>A full-frame plane of the stack's grid normalised on the stack's own disk and moved onto the target, as the stack was.</summary>
    public float[] Registered(ReadOnlySpan<float> plane) =>
        PlanetaryMetrics.Shift(PlanetaryMetrics.Normalise(plane, Width, Height, Own), Width, Height, Target.X - Own.X, Target.Y - Own.Y);

    /// <summary>Stacks <paramref name="input"/> (and its halves when <paramref name="halves"/>) and prepares the window; null, said, when a limb cannot be fitted.</summary>
    public static async Task<PlanetaryWindowedStack?> CreateAsync(IConsoleHost consoleHost, string input, string? truthPath, CatalogIndex planet, int? frames, double keep,
        string telescope, double wavelengthNm, int size, bool halves, CancellationToken ct)
    {
        using var reader = SerReader.Open(input);
        using var whole = new SerFrameStream(reader, ownsReader: false);
        using var stream = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, frames ?? whole.FrameCount));
        if (stream.MidCapture is not { } when || stream.TimestampOf(0) is not { } start || stream.TimestampOf(stream.FrameCount - 1) is not { } end)
        {
            consoleHost.WriteError($"{input}: no timestamps");
            return null;
        }
        var aspect = PhysicalEphemeris.Compute(planet, when);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var options = new PlanetaryStackOptions
        {
            KeepFraction = keep,
            WhitenedCorrelation = false,
            Interpolation = WarpInterpolation.Lanczos3,
            QualityEstimator = new GradientEnergyEstimator(),
        };
        var stacker = new LuckyImagingStacker();
        var result = await stacker.StackGlobalAsync(stream, options, ct);
        PlanetaryStackResult? halfA = null, halfB = null;
        if (halves)
        {
            using var halfAFrames = PlanetaryFrameSubset.Half(stream, 0);
            using var halfBFrames = PlanetaryFrameSubset.Half(stream, 1);
            halfA = await stacker.StackGlobalAsync(halfAFrames, options, ct);
            halfB = await stacker.StackGlobalAsync(halfBFrames, options, ct);
        }
        var prepared = false;
        try
        {
            var stackImage = result.Master;
            var (width, height) = (stackImage.Width, stackImage.Height);
            var truth = truthPath is not null ? PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost) : null;
            if (truthPath is not null && truth is null)
            {
                return null;
            }
            MetricDisk? onto = truth is { } t ? t.Disk with { AxisRatio = limbOptions.AxisRatio } : null;
            if (PlanetaryMeasureSubCommand.Register(stackImage, limbOptions, onto) is not { } stack
                || PlanetaryLimbFit.Fit(stackImage, limbOptions) is not { } fit
                || PlanetaryLimbKernel.Fit(stackImage, fit, limbOptions) is not { } wide)
            {
                consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                return null;
            }
            (float[] Plane, MetricDisk Disk)? a = null, b = null;
            if (halfA is not null && halfB is not null)
            {
                a = PlanetaryMeasureSubCommand.Register(halfA.Master, limbOptions, stack.Disk);
                b = PlanetaryMeasureSubCommand.Register(halfB.Master, limbOptions, stack.Disk);
                if (a is null || b is null)
                {
                    consoleHost.WriteError($"{input}: a half's limb could not be fitted");
                    return null;
                }
            }
            var own = MetricDisk.From(fit, limbOptions.AxisRatio);
            var (originX, originY) = ((int)Math.Round(stack.Disk.X) - (size / 2), (int)Math.Round(stack.Disk.Y) - (size / 2));
            var scale = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;
            var pupil = telescope.ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
            float[] Cut(float[] plane) => PlanetaryInversesSubCommand.Crop(plane, width, height, originX, originY, size);
            var sharp = PlanetaryMetrics.Shift(PlanetaryMetrics.Normalise(PlanetaryLimbFit.SharpModel(fit, limbOptions, width, height), width, height, own), width, height,
                stack.Disk.X - own.X, stack.Disk.Y - own.Y);
            var windowed = new PlanetaryWindowedStack
            {
                Result = result,
                HalfAResult = halfA,
                HalfBResult = halfB,
                Size = size,
                Width = width,
                Height = height,
                OriginX = originX,
                OriginY = originY,
                LimbOptions = limbOptions,
                Fit = fit,
                Wide = wide,
                Own = own,
                Target = stack.Disk,
                Disk = stack.Disk with { X = stack.Disk.X - originX, Y = stack.Disk.Y - originY },
                Stack = Cut(stack.Plane),
                FullStack = stack.Plane,
                FullHalfA = a?.Plane,
                FullHalfB = b?.Plane,
                Start = start,
                End = end,
                HalfA = a is { } ap ? Cut(ap.Plane) : null,
                HalfB = b is { } bp ? Cut(bp.Plane) : null,
                Truth = truth is { } tr ? Cut(PlanetaryMetrics.Normalise(tr.Plane, width, height, stack.Disk)) : null,
                SharpDisk = Cut(sharp),
                Diffraction = PlanetaryInverse.Diffraction(pupil, wavelengthNm * 1e-9, scale),
                ArcsecPerPixel = scale,
                Aspect = aspect,
                When = when,
                Cutoff = pupil.DiameterM / (wavelengthNm * 1e-9) / TianWen.Lib.Imaging.Optics.ShortExposurePsf.ArcsecPerRadian * scale,
            };
            prepared = true;
            return windowed;
        }
        finally
        {
            if (!prepared)
            {
                result.Master.Release();
                halfA?.Master.Release();
                halfB?.Master.Release();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Result.Master.Release();
        HalfAResult?.Master.Release();
        HalfBResult?.Master.Release();
    }
}
