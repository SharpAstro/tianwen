using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The learned sharpening gains TianWen ships (#1552; docs/plans/planetary-restoration.md, "L-GAINS-S"), each a constant written from its
/// training by <c>training/planetary/l_gains_s_model_cs.py</c>, never edited by hand.
/// </summary>
public static class PlanetaryGainsModels
{
    /// <summary>
    /// The EdgeHD 11 Jupiter of 2023-11-11 (a 280 mm pupil 37.5 % obstructed, 0.1032"/px, an ASI678MC at 610, 535 and 460 nm): 40 twins
    /// of that capture (16 of them warped or blurred past what the air gives, as the real stack is), the gains bands 1 to 4 from
    /// the light just outside the limb. It applies only at that sampling (each colour's pupil cutoff within
    /// <see cref="PlanetaryGainsModel.CutoffTolerance"/>) and inside its twins' readings.
    /// </summary>
    public static readonly PlanetaryGainsModel EdgeHd11Jupiter = new("the EdgeHD 11 Jupiter of 2023-11-11, 40 twins", [PlanetaryGainsModel.GlowReading],
    [
        // Red: ridge strength 1, leave-one-out squared error 609.11 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [2.1264036080921587], Spread: [0.23470630687429703], Low: [1.573940492930125], High: [2.6023346011554014],
            Cutoff: 0.229, Weights: [[0.1193946393866178], [-1.489146268661229], [4.060372272405634], [-0.7786987800630563]],
            Intercepts: [-0.10099999999999985, 0.9572499999999975, 11.995750000000008, -0.8245000000000013]),
        // Green: ridge strength 1, leave-one-out squared error 1171.34 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [2.422701398587191], Spread: [0.35650404759452514], Low: [1.8335957971359447], High: [3.2748506483636377],
            Cutoff: 0.261, Weights: [[0.15383093261209227], [-2.0997091007875377], [5.878446119710329], [-1.1559047857782794]],
            Intercepts: [-0.40725, 4.99375, 9.4765, -0.34250000000000025]),
        // Blue: ridge strength 1, leave-one-out squared error 1194.78 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [3.1449559112101704], Spread: [0.43380936016897714], Low: [2.463700642598963], High: [4.194979724597219],
            Cutoff: 0.3035, Weights: [[0.26634708315509564], [-2.7136582925893302], [4.195313054658403], [-0.40840075446412877]],
            Intercepts: [-0.3605, 3.4927499999999996, 10.7915, -0.32099999999999995]),
    ]);
}
