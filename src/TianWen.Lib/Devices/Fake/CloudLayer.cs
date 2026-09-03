namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Which deck the simulated cloud belongs to. The level is not decoration: it is what decides
/// whether a session should shrug, pause, or give the target up, and those are three different
/// behaviours worth being able to test separately.
/// </summary>
/// <remarks>
/// A single coverage dial cannot express this. Coverage says how much of the sky is covered; the
/// layer says what the covering is made of -- how finely structured it is, and how much light it
/// actually stops. Thin cirrus over the whole sky and a closed stratus deck are both "coverage 1.0"
/// and could hardly be less alike to a star detector.
/// </remarks>
internal enum CloudLayer
{
    /// <summary>
    /// High, thin, ice-crystal streaks. Fine structure drawn out along the wind, and only a few
    /// tenths of a magnitude of extinction, so the field survives and the star count dips rather
    /// than collapses. This is the night that quietly degrades instead of ending.
    /// </summary>
    Cirrus,

    /// <summary>
    /// Mid-level cells with real gaps between them: patchy, so the frame-to-frame star count moves
    /// as the pattern drifts. The "clouds rolling in" case, and what the imaging loop's condition
    /// deterioration and recovery path exists for. The default, because it is the interesting one.
    /// </summary>
    Altocumulus,

    /// <summary>
    /// Low, broad, near-featureless deck. Coarse structure and heavy extinction, so where it sits
    /// the sky is simply gone and no recovery is coming. Distinct from the
    /// <c>coverage &gt;= 1.0</c> blackout, which renders no stars at all: a stratus deck at partial
    /// coverage still leaves clear sky beside it.
    /// </summary>
    Stratus,
}

/// <summary>
/// The per-layer constants, in one table so the three decks can be compared at a glance rather than
/// hunted for across the renderer.
/// </summary>
/// <param name="CellSize">Feature size of the coarsest octave, in pixels. Small = finely structured.</param>
/// <param name="StretchX">Horizontal anisotropy; how far the pattern is drawn out along the wind.</param>
/// <param name="Octaves">Octaves of value noise. More = more fine detail on top of the coarse shape.</param>
/// <param name="EdgeSoftness">Noise-value width of the clear-to-opaque ramp at a patch edge.</param>
/// <param name="OpticalDepth">Beer-Lambert depth at full opacity; extinction is <c>exp(-depth)</c>.</param>
/// <param name="GlowScale">Multiplier on the scattered-light glow the deck adds where it is opaque.</param>
internal readonly record struct CloudLayerProfile(
    double CellSize,
    double StretchX,
    int Octaves,
    double EdgeSoftness,
    double OpticalDepth,
    double GlowScale)
{
    /// <summary>
    /// Optical depths are quoted in magnitudes because that is the unit the effect is judged in:
    /// <c>mag = 1.0857 * depth</c>.
    /// <para>
    /// <b>Only cirrus is translucent.</b> That is the one real physical split here, and it is easy to
    /// get wrong: water-droplet cloud is opaque wherever it actually is, so altocumulus differs from
    /// stratus by having GAPS, not by being see-through. Altocumulus was first given depth 3.0 (3.3
    /// mag) on the assumption that "mid-level" meant "half as thick", and that reintroduced the exact
    /// bug this model exists to fix: at high coverage the patches merge into a uniform sheet, 3.3 mag
    /// is not enough to stop a bright star, and the count went back UP -- measured 0.80 -> 29 stars
    /// then 0.95 -> 42. Depth belongs to the droplets; structure belongs to the layer.
    /// </para>
    /// </summary>
    public static CloudLayerProfile For(CloudLayer layer) => layer switch
    {
        // Wispy and strongly wind-drawn: small cells, high anisotropy, an extra octave of detail,
        // and a soft edge because ice cloud has no sharp boundary. 0.76 mag, so the field survives.
        CloudLayer.Cirrus => new CloudLayerProfile(
            CellSize: 64.0, StretchX: 6.0, Octaves: 5, EdgeSoftness: 0.45,
            OpticalDepth: 0.7, GlowScale: 0.35),

        // Opaque where it is, with real gaps between the cells -- the patchiness IS the layer, and
        // it is what makes the star count move frame to frame as the pattern drifts.
        CloudLayer.Altocumulus => new CloudLayerProfile(
            CellSize: 128.0, StretchX: 2.5, Octaves: 4, EdgeSoftness: 0.30,
            OpticalDepth: 5.5, GlowScale: 1.0),

        // Broad and nearly featureless: one coarse octave set, little anisotropy, a harder edge
        // where the deck ends, and enough depth that what it covers is gone.
        CloudLayer.Stratus => new CloudLayerProfile(
            CellSize: 320.0, StretchX: 1.3, Octaves: 3, EdgeSoftness: 0.20,
            OpticalDepth: 6.0, GlowScale: 1.4),

        _ => For(CloudLayer.Altocumulus),
    };
}
