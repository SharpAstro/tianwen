namespace TianWen.Lib.Imaging;

/// <summary>
/// Which rule the star detector uses to drop a detection that is a single warm photosite on a raw
/// mosaic rather than a star (<see cref="Image.FindStarsAsync"/>, RGGB mosaics only).
/// </summary>
/// <remarks>
/// <para><see cref="PeakShare"/> is the shipped guard: a detection whose brightest photosite carries
/// more than <see cref="Image.SinglePhotositeFractionMax"/> of its background-subtracted 3 by 3 flux
/// is a spike (measured 0.92 to 0.99 against 0.15 to 0.41 for stars). It was measured on the
/// unmoved-versus-moved pairing, which admits only detections bright enough to pair, and it does
/// not reach a fainter class: warm pixels whose eight neighbours' NOISE pulls the share to between
/// 0.5 and 0.85, about 2,260 a frame at 12.7 C on the Tarantula L-Ultimate 2025-10-14 night against
/// 1,100 stars, which fill the profile fit's peak-ranked stack and refuse every sub of that night
/// (docs/known-limitations.md, the detector entry).</para>
/// <para><see cref="NeighbourSignificance"/> is the candidate for that class, kept beside the shipped
/// rule so the pre-registered readout can be taken for each: a detection whose share is over
/// <see cref="Image.NeighbourSignificanceShareMin"/> is a spike unless its eight neighbours'
/// background-subtracted sum is significant against their own noise
/// (<see cref="Image.NeighbourSignificanceSigmas"/> times the sum's sigma), because a star of any
/// width the green plane resolves puts real light on its neighbours and a warm photosite puts
/// noise. A faint sharp star with an insignificant neighbourhood is the price, and the readout
/// says what it is.</para>
/// </remarks>
public enum SpikeGuard
{
    /// <summary>The peak photosite's share of the 3 by 3 flux alone (the shipped rule).</summary>
    PeakShare,

    /// <summary>The share, and for a share over 0.5 the neighbours' sum against their noise.</summary>
    NeighbourSignificance,
}
