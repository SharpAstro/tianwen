namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The labels an integrated master carries, stated once for every integration strategy.
/// </summary>
/// <remarks>
/// <para>
/// A master is not a frame off the sensor, so two labels inherited from the source frames are false on
/// it, and each strategy used to hand them over unchanged:
/// </para>
/// <list type="bullet">
/// <item><b><see cref="Image.MaxValue"/> is the OBSERVED peak</b>, as it is for any image. The strategies
/// copied the first source frame's value or hard-coded 1, while every one of them normalises each frame
/// so a channel's sky median lands on <c>NormalizationTarget</c> (0.5) and a star sits tens of times
/// above it. Measured on the real 10P/Tempel 2 set: 61.7 from the tile strategy and 62.0 from the
/// per-colour drizzle, both labelled 1, so the written file claimed <c>DATAMAX = 1</c> over pixels up to
/// 62 and the viewer, trusting the label, clipped every star core flat.</item>
/// <item><b>No sensor full scale.</b> After normalisation no ADU saturation level applies. A light's
/// <see cref="ImageMeta.SensorFullScaleAdu"/> (a live camera's MaxADU, or a <c>SATURATE</c> card) carried
/// onto the master is written back as <c>SATURATE</c>, and <see cref="Image.UnitScaleDivisor"/> prefers it
/// over the peak, so the canonical [0, 1] conversion would divide by the raw ADU instead.</item>
/// </list>
/// <para>
/// With both true, the master goes into unit scale through the ONE canonical path,
/// <see cref="Image.ScaleFloatValuesToUnit"/>, in <see cref="MasterPostProcessor"/>.
/// </para>
/// </remarks>
internal static class IntegratedMaster
{
    /// <summary>
    /// <paramref name="master"/>'s own planes, relabelled: its observed peak as <see cref="Image.MaxValue"/>
    /// and no sensor full scale. No pixel is copied or changed.
    /// </summary>
    internal static Image Labelled(Image master)
    {
        var planes = new float[master.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = master.GetChannelArray(c);
        }

        var (_, observedPeak) = Image.ObservedRange(planes);
        // A master with no finite pixel at all has no peak to state; keep what the strategy said
        // rather than label it with a sentinel.
        var maxValue = float.IsFinite(observedPeak) && observedPeak > float.MinValue ? observedPeak : master.MaxValue;

        return new Image(planes, master.BitDepth, maxValue, master.MinValue, master.Pedestal,
            master.ImageMeta with { SensorFullScaleAdu = null });
    }
}
