using System;
using System.Collections.Immutable;
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
/// Shared state for the guider tab. Reads from <see cref="LiveSessionState"/>
/// (no second ISession poller) — call <see cref="PollFromLiveState"/> each frame.
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

    public bool NeedsRedraw { get; set; }

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
                SessionPhase.CalibratingGuider => GuiderPlaceholder.Calibrating,
                SessionPhase.Cooling or SessionPhase.WaitingForDark
                    or SessionPhase.RoughFocus or SessionPhase.AutoFocus => GuiderPlaceholder.NotGuiding,
                SessionPhase.Observing when LastGuideStats is null => GuiderPlaceholder.WaitingForGuider,
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
    }
}
