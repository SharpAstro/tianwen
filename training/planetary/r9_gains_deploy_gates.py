"""R9 L-GAINS-D's remaining gates on the held twins (RULES.md): G2's per-band clause (no band more than 2 % above the derived gains')
and G3 (the power past 0.9 of each channel's cutoff at most 1.05 times the derived's), each read off the CLI's own sharpening, written
this time. Glue only.

  python deploy_gates.py
"""
import os, re, subprocess, sys
import numpy as np
from astropy.io import fits

sys.path.insert(0, "D:/temp/tianwen-scratch/i1056/deploy")
sys.path.insert(0, "C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen-r9/training/planetary")
import deploy_steps as ds  # noqa: E402
from r9_score import past_cutoff  # noqa: E402
from r9_twin_pairs import offset  # noqa: E402

BANDS = re.compile(r"\] (.*?), ([rgb]) against the truth: transfer ([^;]+); error ([^(]+) \(bands 1 to 4 ([0-9.]+)\)")
CUTOFF = re.compile(r"the pupil's cutoff, each channel: ([0-9.]+), ([0-9.]+), ([0-9.]+) cycles a pixel")


def main():
    p = ds.plan()
    train = [n for n in ds.finished() if not p[n]["held"]]
    held = [n for n in ds.finished() if p[n]["held"]]
    models, _ = ds.fit_channels(train)
    worst_band, g3 = [], []
    for n in held:
        text = open(f"{ds.D}/{n}/sharpen.log", encoding="utf-8", errors="replace").read()
        derived_bands = {m.group(2): [float(v) for v in m.group(4).split(",")[:4]] for m in BANDS.finditer(text) if m.group(1).startswith("derived")}
        cut = [float(v) for v in CUTOFF.search(text).groups()]
        _, _, gains, edges = ds.read_log(n)
        pred = {c: models[c][0](np.array([[*gains[c], *edges[c]]]))[0] for c in ds.CH}
        given = ";".join(",".join(f"{v:.4f}" for v in pred[c]) for c in ds.CH)
        master, truth = ds.twin_paths(n)
        out_dir = f"{ds.D}/gates/{n}"
        os.makedirs(out_dir, exist_ok=True)
        out = subprocess.run([ds.T, "planetary", "sharpen", master, "--truth", truth, *ds.COMMON, *ds.PUPIL, "--gains", given, "-o", out_dir],
                             capture_output=True, text=True).stdout
        open(f"{out_dir}/sharpen.log", "w").write(out)
        learned_bands = {m.group(2): [float(v) for v in m.group(4).split(",")[:4]] for m in BANDS.finditer(out) if "gains given" in m.group(1)}
        for c in ds.CH:
            ratios = [l / d - 1 for l, d in zip(learned_bands[c], derived_bands[c])]
            worst_band.append(max(ratios))
            print(f"{n} {c}: per band, learned against derived " + ", ".join(f"{100 * r:+.1f} %" for r in ratios), flush=True)
        # G3: each channel's power past 0.9 of its cutoff, on the disk (placed by the truth's disk moved onto the master).
        d_img = fits.getdata(f"{ds.D}/{n}/derived/master_{n}_sharpened.fits").astype(float)
        l_img = fits.getdata(f"{out_dir}/{[f for f in os.listdir(out_dir) if f.endswith('_sharpened.fits')][0]}").astype(float)
        for i, c in enumerate(ds.CH):
            t_path = truth.replace(".truth.fits", f".truth.{c}.fits")
            h = fits.getheader(t_path)
            t = fits.getdata(t_path).astype(float)
            x0, y0, r = float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"])
            sl = (slice(int(y0 - 1.3 * r), int(y0 + 1.3 * r)), slice(int(x0 - 1.3 * r), int(x0 + 1.3 * r)))
            dx, dy = offset(d_img[i][sl], t[sl])
            disk = (x0 + dx, y0 + dy, r)
            pd, pl = past_cutoff(d_img[i], disk, 0.9 * cut[i]), past_cutoff(l_img[i], disk, 0.9 * cut[i])
            g3.append(pl / pd)
            print(f"{n} {c}: G3 power past {0.9 * cut[i]:.3f} cycles a pixel, learned over derived {pl / pd:.3f}", flush=True)
    print(f"G2 per band: the worst band over every held twin and channel {100 * max(worst_band):+.1f} % (rule: at most +2 %)")
    print(f"G3: learned over derived, every held twin and channel, {min(g3):.3f} to {max(g3):.3f} (rule: at most 1.05)")


if __name__ == "__main__":
    main()
