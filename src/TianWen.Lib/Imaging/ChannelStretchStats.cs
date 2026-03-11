namespace TianWen.Lib.Imaging;

/// <summary>
/// Per-channel stretch statistics cached from the processed raw image.
/// </summary>
public record struct ChannelStretchStats(float Pedestal, float Median, float Mad);
