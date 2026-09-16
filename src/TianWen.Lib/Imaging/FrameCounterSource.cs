namespace TianWen.Lib.Imaging;

/// <summary>
/// Where a frame's <see cref="ImageMeta.FrameSequence"/> came from. It travels with the number
/// because the two sources answer DIFFERENT questions, and reading one as the other is silently wrong.
/// </summary>
/// <remarks>
/// <para><b>A gap means something only for <see cref="Hardware"/>.</b> A hardware counter is
/// incremented by the camera for every frame the SENSOR produced, so a jump from 41 to 43 is proof
/// that a frame was produced and lost on the way to us (a USB stall, a full DDR buffer, a slow
/// consumer). A software counter is incremented by the driver for every readout the DRIVER
/// completed, so it cannot skip by construction: a frame lost in transit simply never increments it,
/// and the sequence stays dense across the hole. Both are honest ordinals; only one can report a
/// loss.</para>
/// <para>So a consumer counting dropped frames must check this first. Against
/// <see cref="Software"/> the right answer is "cannot tell", never "none".</para>
/// </remarks>
public enum FrameCounterSource
{
    /// <summary>The driver does not track a frame ordinal at all; <see cref="ImageMeta.FrameSequence"/> is -1.</summary>
    None = 0,

    /// <summary>
    /// Counted by the driver, one per completed readout since the camera was opened. Dense by
    /// construction, so it orders frames and identifies the first ones after an open, but it can
    /// never report a dropped frame.
    /// </summary>
    Software = 1,

    /// <summary>
    /// Read from the camera's own frame counter. Gaps are real and mean frames were lost between the
    /// sensor and us.
    /// </summary>
    Hardware = 2,
}
