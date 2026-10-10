using System;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// How a stack carries each frame to one epoch (docs/plans/planetary-restoration.md, R6 part 2): the planet whose rotation it is,
/// and the instant.
/// </summary>
/// <param name="Planet">Mars, Jupiter or Saturn, the planets <see cref="PhysicalEphemeris"/> turns.</param>
public sealed record PlanetaryDerotationOptions(CatalogIndex Planet)
{
    /// <summary>The instant every frame is carried to; the capture's middle (<see cref="PlanetaryFrameStreamExtensions"/>) when null.</summary>
    public DateTimeOffset? Epoch { get; init; }

    /// <summary>
    /// Turn the limb fit's north over. Near opposition the planet is lit almost evenly, and the fit's north, which only its sun
    /// side decides, can be the south (R5a saw it turned over 175 degrees between two runs of one capture); turned the wrong way,
    /// a de-rotation turns the planet backwards. Two stacks' agreement settles it (<c>tianwen planetary derotate</c>).
    /// </summary>
    public bool TurnNorthOver { get; init; }

    /// <summary>
    /// A north its caller decided, degrees as <see cref="DiskPlacement.NorthAngleDeg"/> (#1347): a session's, read once from its first
    /// and last captures hours apart (<see cref="LuckyImagingStacker.ReadNorthAsync"/>), where one capture's own quarters, minutes apart,
    /// can tie on a bland globe (one file of 30 of the owner's 2026-10-07 Saturn read it turned over, 0.02008 against 0.02009). The
    /// capture's quarters are then not read: the limb fit's axis is kept, and of its two ways round the one nearer this. Null, the
    /// default, reads the capture's own.
    /// </summary>
    public double? North { get; init; }

    /// <summary>
    /// How far apart in time the global aligner's references are, seconds: each frame is registered against the reference turned
    /// to the nearest of them, since a reference taken minutes away has its belts where the frame's are not, and a whole-disk
    /// correlation would split the difference between them and the limb. Ten seconds turns Jupiter 0.1 degrees, 0.04 px of a
    /// 24 px disk's middle.
    /// </summary>
    public double ReferenceStepSeconds { get; init; } = 10;

    /// <summary>
    /// The least the planet's turn over the capture must move the middle of its disk, px, for the stack to carry its frames to one
    /// epoch (0, the default, carries any capture's, and fails one without frame times). A turn under a pixel moves a frame less than
    /// the registration places it (0.2 to 0.35 px, R5), and a de-rotation costs a limb fit and a field per frame; such a capture, or
    /// one without frame times, is stacked as taken (<see cref="PlanetaryStackResult.TurnPx"/> says how far it turned).
    /// </summary>
    public double MinimumTurnPx { get; init; }
}

/// <summary>
/// Each frame of a capture carried to one epoch as it is stacked (<see cref="PlanetaryDerotationOptions"/>): the capture's disk (the
/// limb fit of its best frames stacked as taken, which a rotation leaves where it is) is the stack's, every frame is registered against the reference turned
/// to the frame's own instant, and its de-rotation to the epoch (<see cref="DerotationField"/>) goes beneath its registration in
/// the mesh the stack resamples it by. The stack visits its frames in capture order, so one turned reference serves a run of
/// them. One frame at a time: the field is refilled for every frame.
/// </summary>
internal sealed class FrameDerotator
{
    private readonly IPlanetaryFrameStream _stream;
    private readonly CatalogIndex _planet;
    private readonly DerotationTarget _toEpoch;
    private readonly DerotationField _field;
    private readonly double _stepSeconds;
    private readonly int _alignTileSize;
    private readonly bool _whiten;
    private Image? _template;
    private (long Step, GlobalAligner Aligner)? _aligner;

    private FrameDerotator(IPlanetaryFrameStream stream, CatalogIndex planet, PlanetAspect epoch, DiskPlacement placement, double minnaertK,
        int width, int height, double stepSeconds, int alignTileSize, bool whiten)
    {
        (_stream, _planet, Epoch, Placement, MinnaertK) = (stream, planet, epoch, placement, minnaertK);
        (_stepSeconds, _alignTileSize, _whiten) = (stepSeconds, alignTileSize, whiten);
        _toEpoch = new DerotationTarget(epoch, placement, width, height, minnaertK);
        _field = new DerotationField(width, height);
    }

    /// <summary>The planet at the epoch every frame is carried to.</summary>
    public PlanetAspect Epoch { get; }

    /// <summary>The stack's disk: the limb fit of the capture's best frames stacked as taken, its north turned over if asked.</summary>
    public DiskPlacement Placement { get; }

    /// <summary>The capture's limb darkening, Minnaert's k from the same fit, which relights every carried sample.</summary>
    public double MinnaertK { get; }

    /// <summary>The reference at the epoch every frame is registered against (<see cref="UseTemplate"/>); this derotator's, never to be released by a caller.</summary>
    public Image Template => _template ?? throw new InvalidOperationException("The derotator has no reference yet.");

    /// <summary>
    /// The de-rotation for <paramref name="stream"/> on the disk <paramref name="disk"/> shows, an image on the grid of its best
    /// frame (frame <paramref name="bestIndex"/>, whose instant the limb fit's lighting is read at): a stack of its best frames as
    /// taken, which the stacker fits instead of one frame. Read but not kept: <see cref="UseTemplate"/> gives it the reference to
    /// register against. Its north is the limb fit's, which the stacker then checks against the capture itself
    /// (<see cref="TurnedOver"/>).
    /// </summary>
    public static FrameDerotator Create(IPlanetaryFrameStream stream, Image disk, int bestIndex, PlanetaryDerotationOptions options, int alignTileSize, bool whiten)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReferenceStepSeconds);
        if (!PhysicalEphemeris.Supports(options.Planet))
        {
            throw new ArgumentException($"{options.Planet} has no rotation model to de-rotate by; Jupiter and Saturn have.", nameof(options));
        }
        if (!stream.HasTimestamps || stream.TimestampOf(bestIndex) is not { } bestTime || (options.Epoch ?? stream.MidCapture) is not { } epochTime)
        {
            throw new InvalidOperationException("A de-rotated stack needs every frame's time, and this capture has none.");
        }
        var bestAspect = PhysicalEphemeris.Compute(options.Planet, bestTime);
        if (PlanetaryLimbFit.Fit(disk, PlanetaryLimbFit.OptionsFor(bestAspect)) is not { } fit)
        {
            throw new InvalidOperationException($"The capture's limb (on frame {bestIndex}'s grid) could not be fitted, so its disk cannot be de-rotated.");
        }
        var placement = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg + (options.TurnNorthOver ? 180 : 0));
        return new FrameDerotator(stream, options.Planet, PhysicalEphemeris.Compute(options.Planet, epochTime), placement, fit.LimbDarkening,
            disk.Width, disk.Height, options.ReferenceStepSeconds, alignTileSize, whiten);
    }

    /// <summary>
    /// How far <paramref name="planet"/>'s turn over <paramref name="stream"/> moves the middle of its disk, px on
    /// <paramref name="frame"/>'s grid: the central meridian's change from the capture's earliest frame to its latest
    /// (<c>CaptureSpan</c>, whatever order the frames come in, #1292), in radians, times the disk's
    /// equatorial radius (<see cref="PlanetaryLimbFit.Start"/>; Saturn's globe from its rings' reach, <see cref="PlanetaryLimbFit.StartRinged"/>,
    /// which the bright area would put 2.3 times too large) and the cosine of the latitude it is seen from. NaN when the capture has no frame
    /// times, the planet no rotation model, or the frame no disk.
    /// </summary>
    internal static double TurnAtCentrePx(IPlanetaryFrameStream stream, CatalogIndex planet, Image frame)
    {
        if (!PhysicalEphemeris.Supports(planet) || stream.FrameCount < 2 || stream.CaptureSpan is not { } span)
        {
            return double.NaN;
        }
        var (from, to) = (PhysicalEphemeris.Compute(planet, span.Earliest), PhysicalEphemeris.Compute(planet, span.Latest));
        var turnDeg = Math.Abs(Math.IEEERemainder(to.CentralMeridianIII - from.CentralMeridianIII, 360));
        var options = PlanetaryLimbFit.OptionsFor(from);
        var start = options.Rings is { } rings
            ? PlanetaryLimbFit.StartRinged(frame.GetChannelSpan(0), frame.Width, frame.Height, rings)
            : PlanetaryLimbFit.Start(frame.GetChannelSpan(0), frame.Width, frame.Height, options.AxisRatio);
        return start is { } disk
            ? disk.Radius * turnDeg * Math.PI / 180 * Math.Cos(from.SubObserverLatitudeCentric * Math.PI / 180)
            : double.NaN;
    }

    /// <summary>The same de-rotation with the disk's north turned over, as a derotator of its own with no reference yet.</summary>
    public FrameDerotator TurnedOver()
        => new(_stream, _planet, Epoch, Placement with { NorthAngleDeg = Placement.NorthAngleDeg + 180 }, MinnaertK,
            _field.Width, _field.Height, _stepSeconds, _alignTileSize, _whiten);

    /// <summary>The planet at frame <paramref name="index"/>'s instant.</summary>
    public PlanetAspect AspectOf(int index) => PhysicalEphemeris.Compute(_planet, TimeOf(index));

    /// <summary><paramref name="image"/>, taken at <paramref name="at"/> on the stack's disk, carried to the epoch: a new image, the caller's.</summary>
    public Image ToEpoch(Image image, in PlanetAspect at) => PlanetaryDerotation.Resample(image, _toEpoch.FieldFrom(at, Placement));

    /// <summary>
    /// Registers every later frame against <paramref name="template"/>, the planet at the epoch on the stack's disk, turned to each
    /// frame's instant. Takes the image, which it keeps, and releases the one it had.
    /// </summary>
    public void UseTemplate(Image template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _template?.Release();
        (_template, _aligner) = (template, null);
    }

    /// <summary>
    /// Frame <paramref name="index"/>'s shift onto the stack's disk, as <see cref="GlobalAligner.Estimate"/> states it, measured
    /// against the reference turned to the reference instant nearest the frame's.
    /// </summary>
    public PhaseCorrelation.Shift Shift(Image frame, int index)
    {
        var template = Template;
        var step = (long)Math.Round((TimeOf(index) - Epoch.Utc).TotalSeconds / _stepSeconds);
        if (_aligner is not { } current || current.Step != step)
        {
            var at = PhysicalEphemeris.Compute(_planet, Epoch.Utc + TimeSpan.FromSeconds(step * _stepSeconds));
            var turned = PlanetaryDerotation.Resample(template, new DerotationTarget(at, Placement, template.Width, template.Height, MinnaertK).FieldFrom(Epoch, Placement));
            current = (step, LuckyImagingStacker.AlignerFor(turned, PlanetaryDisk.BoundingBox(turned), _alignTileSize, _whiten));
            turned.Release();
            _aligner = current;
        }
        return current.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
    }

    /// <summary>
    /// Where each pixel of the stack reads frame <paramref name="index"/> once the frame's disk is registered onto the stack's, and
    /// how its sample is relit: the field this derotator refills for every frame, so it holds only until the next call.
    /// </summary>
    public DerotationField FieldFor(int index)
    {
        _toEpoch.FillFrom(AspectOf(index), Placement, _field);
        return _field;
    }

    private DateTimeOffset TimeOf(int index)
        => _stream.TimestampOf(index) ?? throw new InvalidOperationException($"Frame {index} has no time to de-rotate it from.");
}

/// <summary>
/// Which way round a de-rotated stack took the planet's north (docs/plans/planetary-restoration.md, R6 part 2): the north it used,
/// and how far apart the best frames of the capture's first and last quarters were once the earlier was carried to the later's
/// instant with the limb fit's north and with it turned over (<see cref="PlanetaryDerotation.DifferenceRms"/>). Both are NaN
/// for a capture the planet turned too little in to tell, where the fit's north is kept, and for a north its caller gave
/// (<see cref="Given"/>).
/// </summary>
public sealed record PlanetaryNorthDecision(double NorthAngleDeg, double AgreementAsFitted, double AgreementTurnedOver)
{
    /// <summary>Whether the fit's north was turned over: the capture agreed better that way round.</summary>
    public bool TurnedOver => AgreementTurnedOver < AgreementAsFitted;

    /// <summary>
    /// The north the caller gave (<see cref="PlanetaryDerotationOptions.North"/>, a session's, #1347), which the capture took instead
    /// of reading its own quarters; null when its quarters decided.
    /// </summary>
    public double? Given { get; init; }
}
