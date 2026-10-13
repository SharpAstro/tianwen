using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The learned sharpening gains TianWen ships (#1552; docs/plans/planetary-restoration.md, "L-GAINS-S"), each a constant written from its
/// training by <c>training/planetary/l_gains_s_model_cs.py</c>, never edited by hand.
/// </summary>
public static class PlanetaryGainsModels
{
    /// <summary>
    /// The EdgeHD 11 Jupiter of 2023-11-11 (a 280 mm pupil 37.5 % obstructed, 0.1032"/px, an ASI678MC at 610, 535 and 460 nm): 24 twins
    /// of that capture (0 of them warped or blurred past what the air gives, as the real stack is), the gains bands 1 to 4 from
    /// the limb's edge at 0.1 cycles a pixel. It applies only at that sampling (each colour's pupil cutoff within
    /// <see cref="PlanetaryGainsModel.CutoffTolerance"/>) and inside its twins' readings.
    /// </summary>
    public static readonly PlanetaryGainsModel EdgeHd11Jupiter = new("the EdgeHD 11 Jupiter of 2023-11-11, 24 twins", [PlanetaryGainsModel.EdgeReading],
    [
        // Red: ridge strength 1, leave-one-out squared error 46.35 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [0.4527916666666667], Spread: [0.039451424950620706], Low: [0.4], High: [0.537],
            Cutoff: 0.23, Weights: [[-0.042187322819497115], [0.5700212357156091], [-2.350462206470624], [0.44303722926934813]],
            Intercepts: [-0.08375000000000005, 1.4287500000000004, 5.544583333333332, 0.6262500000000003]),
        // Green: ridge strength 0.3, leave-one-out squared error 13.79 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [0.4990833333333333], Spread: [0.05564389504399242], Low: [0.41], High: [0.601],
            Cutoff: 0.262, Weights: [[0.029054417542098664], [-0.28437867061452216], [-1.0093129818057174], [0.2112184602420926]],
            Intercepts: [-0.19291666666666665, 3.4158333333333326, 3.2295833333333333, 1.0308333333333335]),
        // Blue: ridge strength 1, leave-one-out squared error 35.12 over the training twins.
        new PlanetaryGainsModel.Colour(Mean: [0.36333333333333334], Spread: [0.04995970598714669], Low: [0.285], High: [0.492],
            Cutoff: 0.3045, Weights: [[-0.015697984029341118], [0.3256918019389107], [-1.8101601056941503], [0.45116625265966426]],
            Intercepts: [-0.2266666666666667, 2.7841666666666667, 5.638333333333334, 0.6408333333333335]),
    ]);
}
