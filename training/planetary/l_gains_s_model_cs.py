"""Writes the trained learned-gains model (gains-s-model.json, gains_s.py train) as the reviewed constant the sharpening ships:
src/TianWen.Lib/Imaging/Planetary/PlanetaryGainsModels.cs in the given worktree. Every number is written at its full precision (Python's
repr round-trips a double), so the shipped Predict is the trained arithmetic exactly.

  python l_gains_s_model_cs.py <model.json> <worktree>
"""
import json, sys

model = json.load(open(sys.argv[1]))
out = f"{sys.argv[2]}/src/TianWen.Lib/Imaging/Planetary/PlanetaryGainsModels.cs"


def arr(values):
    return "[" + ", ".join(repr(float(v)) for v in values) + "]"


colours = []
for name, c in zip(("Red", "Green", "Blue"), model["colours"]):
    weights = "[" + ", ".join(arr(w) for w in c["weights"]) + "]"
    colours.append(f"""        // {name}: ridge strength {c['strength']}, leave-one-out squared error {c['loo']:.2f} over the training twins.
        new PlanetaryGainsModel.Colour(Mean: {arr(c['mean'])}, Spread: {arr(c['spread'])}, Low: {arr(c['low'])}, High: {arr(c['high'])},
            Cutoff: {c['cutoff']!r}, Weights: {weights},
            Intercepts: {arr(c['intercepts'])}),""")
twins = len(model["twins"])
words = " and ".join({"edge": "the limb's edge at 0.1 cycles a pixel", "glow": "the light just outside the limb"}[r] for r in model["readings"])
readings = ", ".join({"edge": "PlanetaryGainsModel.EdgeReading", "glow": "PlanetaryGainsModel.GlowReading"}[r] for r in model["readings"])
warped = sum(t[0] in "ef" for t in model["twins"])
text = f"""using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The learned sharpening gains TianWen ships (#1552; docs/plans/planetary-restoration.md, "L-GAINS-S"), each a constant written from its
/// training by <c>training/planetary/l_gains_s_model_cs.py</c>, never edited by hand.
/// </summary>
public static class PlanetaryGainsModels
{{
    /// <summary>
    /// The EdgeHD 11 Jupiter of 2023-11-11 (a 280 mm pupil 37.5 % obstructed, 0.1032"/px, an ASI678MC at 610, 535 and 460 nm): {twins} twins
    /// of that capture ({warped} of them warped or blurred past what the air gives, as the real stack is), the gains bands 1 to 4 from
    /// {words}. It applies only at that sampling (each colour's pupil cutoff within
    /// <see cref="PlanetaryGainsModel.CutoffTolerance"/>) and inside its twins' readings.
    /// </summary>
    public static readonly PlanetaryGainsModel EdgeHd11Jupiter = new("{model['name']}", [{readings}],
    [
{chr(10).join(colours)}
    ]);
}}
"""
open(out, "w", encoding="utf-8", newline="\n").write(text)
print(f"wrote {out}")
