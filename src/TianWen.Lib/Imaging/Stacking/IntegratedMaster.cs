namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The labels an integrated master carries, stated once for every integration strategy.
/// </summary>
/// <remarks>
/// <para>
/// A master is not a frame off the sensor, so the labels inherited from the source frames are false on
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
/// <item><b>The pedestal is the integration's, not the first frame's.</b> The normaliser maps each
/// frame's pedestal to zero, <c>(in - pedestal) * scale</c>, so a master of normalised frames has none,
/// yet five strategies copied the first frame's onto it and the post-processor wrote that, scaled, as
/// <c>PEDESTAL</c>, which the display subtracts before its curve. Only the strategy knows whether its
/// frames went through the normaliser, so it says so.</item>
/// </list>
/// <para>
/// <b>The black point (<see cref="Image.MinValue"/>) is the strategy's stated zero, deliberately NOT the
/// observed minimum.</b> It is not a range statistic to the consumers that read it: the display takes its
/// pedestal from it (<see cref="Image.GetPedestralMedianAndMADScaledToUnit"/>), and the FITS reader
/// treats a negative <c>DATAMIN</c> as unusable. On the real 10P/Tempel 2 drizzle master the darkest
/// finite pixel is 69 percent of the sky median (0.00488 against 0.00712, measured 2026-09-17): labelled
/// as the black point it would lift the display's zero most of the way to the sky, off one pixel, which
/// is the extremum-as-mode-switch the unit-scale predicate had to be cured of. The standard tile
/// master's minimum is exact zero only because its uncovered canvas ring is.
/// </para>
/// <para>
/// With all of these true, the master goes into unit scale in <see cref="MasterPostProcessor"/>, through
/// <see cref="Image.ScaleFloatValuesToUnitCeiling"/>.
/// </para>
/// </remarks>
internal static class IntegratedMaster
{
    /// <summary>
    /// <paramref name="master"/>'s own planes, relabelled: its observed peak as <see cref="Image.MaxValue"/>,
    /// no sensor full scale, and, when <paramref name="normalised"/>, no pedestal and a black point of
    /// zero. No pixel is copied or changed.
    /// </summary>
    /// <param name="master">The strategy's master, labelled with what the strategy knows.</param>
    /// <param name="normalised">
    /// Every frame went through the normaliser, which maps its pedestal to zero. False keeps the
    /// pedestal and black point the strategy stated, which must then already be in the master's units.
    /// </param>
    internal static Image Labelled(Image master, bool normalised)
    {
        var planes = Planes(master);
        var (_, observedPeak) = Image.ObservedRange(planes);
        // A master with no finite pixel at all has no peak to state; keep what the strategy said
        // rather than label it with NaN.
        var maxValue = float.IsNaN(observedPeak) ? master.MaxValue : observedPeak;

        return new Image(planes, master.BitDepth, maxValue,
            normalised ? 0f : master.MinValue,
            normalised ? 0f : master.Pedestal,
            master.ImageMeta with { SensorFullScaleAdu = null });
    }

    /// <summary>
    /// The comet composite: <paramref name="planes"/>, which are <paramref name="layer"/>'s own pixels with
    /// the body added back, labelled as a master. Adding the body moves pixels, not the zero they are measured
    /// from, so the black point and pedestal are <paramref name="layer"/>'s, and only the peak is observed
    /// afresh.
    /// </summary>
    /// <remarks>
    /// This used to label the composite's black point with its observed minimum, the one master that did,
    /// so the same stars rendered against a different zero in the composite than in the star layer beside
    /// it. See the class remarks for why a minimum is not a black point.
    /// </remarks>
    internal static Image Composite(Image layer, float[][,] planes)
        => Labelled(new Image(planes, BitDepth.Float32, layer.MaxValue, layer.MinValue, layer.Pedestal, layer.ImageMeta), normalised: false);

    private static float[][,] Planes(Image image)
    {
        var planes = new float[image.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = image.GetChannelArray(c);
        }

        return planes;
    }
}
