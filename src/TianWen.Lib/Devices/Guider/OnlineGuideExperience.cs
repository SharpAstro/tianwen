namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// A single experience tuple recorded during guided frames for online training.
/// Contains the feature vector snapshot, the initial P-controller target, and
/// (once observed) the hindsight-optimal target computed from the next-frame error.
/// </summary>
internal struct OnlineGuideExperience
{
    /// <summary>
    /// Snapshot of the feature vector at the time of this frame.
    /// </summary>
    public float[] Features;

    /// <summary>
    /// Training target RA correction, normalized to [-1, 1].
    /// Initially set to the P-controller output; replaced with the hindsight-optimal
    /// correction once the next-frame error is observed.
    /// </summary>
    public float TargetRa;

    /// <summary>
    /// Training target Dec correction, normalized to [-1, 1].
    /// Initially set to the P-controller output; replaced with the hindsight-optimal
    /// correction once the next-frame error is observed.
    /// </summary>
    public float TargetDec;

    /// <summary>
    /// Actual RA correction applied this frame, normalized to [-1, 1].
    /// Used to compute the hindsight-optimal target when the next-frame error is known.
    /// </summary>
    public float AppliedRaNorm;

    /// <summary>
    /// Actual Dec correction applied this frame, normalized to [-1, 1].
    /// </summary>
    public float AppliedDecNorm;

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
