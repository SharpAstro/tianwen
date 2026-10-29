using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Placeholder reason when the guider tab cannot show guide data.
/// </summary>
public enum GuiderPlaceholder
{
    NoSession,
    Connecting,
    Calibrating,
    NotGuiding,
    WaitingForGuider
}

/// <summary>
/// The guide star chosen on the looped guide camera (#1915): where it was last measured, in frame pixels (0-based, sub-pixel,
/// the centroid). An immutable value, re-centred on each frame's centroid, which whatever starts guiding on the chosen star
/// reads (<see cref="GuiderTabState.Selection"/>).
/// </summary>
public readonly record struct GuideStarSelection(double X, double Y);

/// <summary>
/// Shared state for the guider tab. Reads from <see cref="LiveSessionState"/>
/// (no second ISession poller): call <see cref="PollFromLiveState"/> each frame.
/// </summary>
public class GuiderTabState
{
    public ImmutableArray<GuideErrorSample> GuideSamples { get; private set; } = [];
    public GuideStats? LastGuideStats { get; private set; }
    public SessionPhase Phase { get; private set; }
    public bool IsRunning { get; private set; }
    public string? CurrentActivity { get; private set; }
    public ScheduledObservation? ActiveObservation { get; private set; }
    public string? GuiderState { get; private set; }
    public SettleProgress? GuiderSettleProgress { get; private set; }
    public TimeSpan GuideExposure { get; private set; }
    public Image? LastGuideFrame { get; private set; }
    public (double X, double Y)? GuideStarPosition { get; private set; }
    public double? GuideStarSNR { get; private set; }
    public (float[] H, float[] V)? GuideStarProfile { get; private set; }
    public CalibrationOverlayData? CalibrationOverlay { get; private set; }

    /// <summary>What the profile's guide focuser reads (<see cref="LiveSessionState.GuideFocuser"/>); null when none is assigned and connected.</summary>
    public FocuserReading? GuideFocuser { get; private set; }

    public bool NeedsRedraw { get; set; }

    /// <summary>
    /// The guide star chosen while looping, re-centred on each frame's centroid; null until one is picked. It is kept when the
    /// star is lost (<see cref="Readout"/> null), so the box stays where the star last was.
    /// </summary>
    public GuideStarSelection? Selection { get; private set; }

    /// <summary>What the chosen star reads in the latest looped frame; null with no selection, or when the star is not found.</summary>
    public GuideStarMeasurement? Readout { get; private set; }

    /// <summary>A press on the frame, to resolve against the next frame (frame pixels); consumed by <see cref="UpdateReadout"/>.</summary>
    private (double X, double Y)? _pendingPick;

    private bool _pendingAuto;

    /// <summary>Picks the star nearest the frame point <paramref name="x"/>, <paramref name="y"/> (a click, mapped by the viewer).</summary>
    public void SelectStar(double x, double y)
    {
        _pendingPick = (x, y);
        _pendingAuto = false;
        NeedsRedraw = true;
    }

    /// <summary>Picks the brightest unsaturated isolated star (the Auto button).</summary>
    public void SelectAutoStar()
    {
        _pendingAuto = true;
        _pendingPick = null;
        NeedsRedraw = true;
    }

    /// <summary>Drops the selection, its readout and anything not yet resolved.</summary>
    public void ClearSelection()
    {
        _pendingPick = null;
        _pendingAuto = false;
        Selection = null;
        Readout = null;
        NeedsRedraw = true;
    }

    /// <summary>
    /// Reads the chosen star in the looped <paramref name="source"/>: a pending pick or Auto against the whole frame, else, on
    /// each <paramref name="isNewFrame"/>, the selection followed into it. A star not found leaves the selection and says so
    /// through a null <see cref="Readout"/>. The full scale of a preview source is 1: its samples are unit-referred.
    /// </summary>
    public void UpdateReadout(IPreviewSource? source, bool isNewFrame)
    {
        if (source is not { Width: > 0, Height: > 0 })
        {
            return;
        }

        if (_pendingPick is { } pick)
        {
            _pendingPick = null;
            Readout = GuideStarReadout.Measure(source.GetChannelData(0), source.Width, source.Height, 1f, pick);
            Selection = Readout is { } picked ? new GuideStarSelection(picked.X, picked.Y) : Selection;
        }
        else if (_pendingAuto)
        {
            _pendingAuto = false;
            Readout = GuideStarReadout.Measure(source.GetChannelData(0), source.Width, source.Height, 1f);
            Selection = Readout is { } auto ? new GuideStarSelection(auto.X, auto.Y) : Selection;
        }
        else if (isNewFrame && Selection is { } last)
        {
            Readout = GuideStarReadout.Track(source.GetChannelData(0), source.Width, source.Height, 1f, (last.X, last.Y));
            Selection = Readout is { } tracked ? new GuideStarSelection(tracked.X, tracked.Y) : Selection;
        }
        else
        {
            return;
        }

        NeedsRedraw = true;
    }

    /// <summary>
    /// Returns the placeholder reason if guide data cannot be shown, or null if guiding is active.
    /// </summary>
    public GuiderPlaceholder? PlaceholderReason
    {
        get
        {
            if (!IsRunning)
            {
                return GuiderPlaceholder.NoSession;
            }

            return Phase switch
            {
                SessionPhase.Initialising => GuiderPlaceholder.Connecting,
                SessionPhase.CalibratingGuider when LastGuideFrame is null => GuiderPlaceholder.Calibrating,
                SessionPhase.Cooling or SessionPhase.WaitingForDark
                    or SessionPhase.RoughFocus or SessionPhase.AutoFocus => GuiderPlaceholder.NotGuiding,
                SessionPhase.Observing when LastGuideStats is null && LastGuideFrame is null => GuiderPlaceholder.WaitingForGuider,
                _ => null
            };
        }
    }

    /// <summary>
    /// Polls guide-relevant fields from the shared live session state. Cheap struct copies.
    /// </summary>
    public void PollFromLiveState(LiveSessionState live)
    {
        GuideSamples = live.GuideSamples;
        LastGuideStats = live.LastGuideStats;
        Phase = live.Phase;
        IsRunning = live.IsRunning;
        CurrentActivity = live.CurrentActivity;
        ActiveObservation = live.ActiveObservation;
        GuiderState = live.GuiderState;
        GuiderSettleProgress = live.GuiderSettleProgress;
        GuideExposure = live.GuideExposure;
        LastGuideFrame = live.LastGuideFrame;
        GuideStarPosition = live.GuideStarPosition;
        GuideStarSNR = live.GuideStarSNR;
        GuideStarProfile = live.GuideStarProfile;
        CalibrationOverlay = live.CalibrationOverlay;
        GuideFocuser = live.GuideFocuser;
    }
}
