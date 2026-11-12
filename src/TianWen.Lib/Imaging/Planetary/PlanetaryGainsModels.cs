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
        // Red: ridge strength 1, leave-one-out squared error 291.48 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [2.1264036080921587], Spread: [0.23470630687429703], Low: [1.573940492930125], High: [2.6023346011554014],
            Cutoff: 0.229, Weights: [[0.07252009593240068], [-1.015367658387255], [2.649323389318078], [-0.4228631122878871]],
            Intercepts: [-0.04499999999999988, 0.7214999999999985, 6.875000000000006, 0.35174999999999923]),
        // Green: ridge strength 1, leave-one-out squared error 545.93 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [2.422701398587191], Spread: [0.35650404759452514], Low: [1.8335957971359447], High: [3.2748506483636377],
            Cutoff: 0.261, Weights: [[0.07304610811985185], [-1.3910268259582275], [4.155871147012841], [-0.7381669235025878]],
            Intercepts: [-0.15775000000000006, 2.5334999999999996, 6.306500000000002, 0.3834999999999998]),
        // Blue: ridge strength 1, leave-one-out squared error 606.34 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [3.1449559112101704], Spread: [0.43380936016897714], Low: [2.463700642598963], High: [4.194979724597219],
            Cutoff: 0.3035, Weights: [[0.14775015815315967], [-1.6543378458734803], [3.0699280906920348], [-0.2348723037054464]],
            Intercepts: [-0.11800000000000005, 1.40475, 7.7524999999999995, 0.36225]),
    ]);
}
