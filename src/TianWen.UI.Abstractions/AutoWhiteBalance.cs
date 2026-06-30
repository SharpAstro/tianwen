using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// <see cref="IPreviewSource"/> adapter for gray-world auto white-balance. Dispatches a live preview frame
/// (raw 1-channel RGGB mosaic or a de-interleaved 3-channel colour source) into the pure
/// <see cref="GrayWorldWhiteBalance"/> math in <c>TianWen.Lib</c>, returning a triple fed into
/// <see cref="ViewerState.ManualWhiteBalance"/>; the manual sliders then act as the fine-tune / escape hatch
/// (e.g. warming a planet that gray-world rendered a touch cold).
/// <para>
/// This is the planetary-friendly counterpart to the star-based photometric calibration (Tycho-2 / SPCC),
/// which needs field stars and so does nothing on a planetary SER.
/// </para>
/// </summary>
public static class AutoWhiteBalance
{
    /// <summary>
    /// Computes gray-world WB multipliers for the current frame of <paramref name="source"/>, or null when
    /// the source is not colour (mono) or has no illuminated pixels. Handles both a raw Bayer mosaic
    /// (1-channel RGGB, sampled at its CFA sites) and a de-interleaved 3-channel colour source.
    /// </summary>
    public static (float R, float G, float B)? GrayWorld(IPreviewSource source)
    {
        if (source.SensorType is SensorType.RGGB && source.ChannelCount == 1)
        {
            return GrayWorldWhiteBalance.GrayWorldBayer(source.GetChannelData(0), source.Width, source.Height,
                source.BayerOffsetX, source.BayerOffsetY);
        }
        if (source.ChannelCount >= 3)
        {
            return GrayWorldWhiteBalance.GrayWorldRgb(source.GetChannelData(0), source.GetChannelData(1), source.GetChannelData(2));
        }
        return null;
    }
}
