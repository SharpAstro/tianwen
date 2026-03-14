namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// A single experience tuple recorded during guided frames for online training.
/// Contains the feature vector snapshot and the P-controller's target output.
/// </summary>
internal struct OnlineGuideExperience
{
    /// <summary>
    /// Snapshot of the feature vector at the time of this frame.
    /// </summary>
    public float[] Features;

    /// <summary>
    /// P-controller target RA correction, normalized to [-1, 1].
    /// </summary>
    public float TargetRa;

    /// <summary>
    /// P-controller target Dec correction, normalized to [-1, 1].
    /// </summary>
    public float TargetDec;

    /// <summary>
    /// Importance weight for experience replay. Initialized to 1.0,
    /// updated after the next-frame error is observed.
    /// </summary>
    public float PriorityWeight;

    /// <summary>
    /// Whether the outcome (next-frame error) has been observed.
    /// </summary>
    public bool OutcomeKnown;
}
