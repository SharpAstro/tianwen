"""Pins real_star_check.py's per-session table with no GPU and no owner data: a stub export of two validation sessions (one with a
flip half, as r2-val.txt names them) and two columns ('input' and one model), built in a temp dir that is removed afterwards.
    python -I test_real_star_sessions.py
Exit 0 when the pooled line and every per-session row are as the stub's own arithmetic says, 1 and the output otherwise.
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
SIZE, SCALE = 256, 0.6745 * 100.0
LOBSTER = "QHYCCD-QHY294C/IDAS-LPS-D3/Lobster-Nebula/2026-08-01|QHY294PROC|Lobster Nebula|IDAS LPS D3"
M42 = "ZWO-ASI294MC/UV-IR-Cut/Great-Orion-Nebula/2021-11-28|ZWO ASI294MC|Orion M42 RGB 4min|UV/IR Cut"
# Each tile: its session id and the model's residual (in sigma) at each of its stars, which sit 40 px apart inside the rim.
# Input is +5 at every star (all left). Model "m" leaves 0 (flat), +5 (left) or -5 (dug).
TILES = [
    ("lobster-night", LOBSTER, [0, 0, 5, 5]),
    ("lobster-a", LOBSTER + "|flip=a", [-5]),
    ("m42", M42, [0, 0, 5]),
]
POSITIONS = [(60, 60), (100, 100), (140, 140), (180, 180)]
# One star in each of the two fainter bands, in the first tile only, so every band of the pooled table has readings.
FAINT = [(180, 60, 10), (60, 180, 50)]


def tile(levels, faint=0):
    img = np.zeros((3, SIZE, SIZE), np.float32)
    for (x, y), v in zip(POSITIONS, levels):
        img[:, y - 1:y + 2, x - 1:x + 2] = v
    for x, y, _ in FAINT:
        img[:, y - 1:y + 2, x - 1:x + 2] = faint
    return img.astype("<f2").tobytes()


def build(root):
    export, plates, evals = root / "export", root / "plates", root / "eval"
    (export / "tiles").mkdir(parents=True)
    plates.mkdir()
    (evals / "m" / "on-real-val").mkdir(parents=True)
    injections, outputs = [], []
    for name, sid, levels in TILES:
        t = f"tiles/{name}.f16"
        faint = FAINT if name == "lobster-night" else []
        (export / t).write_bytes(tile([5] * len(levels), 5 if faint else 0))
        (export / f"tiles/{name}.sigma.f16").write_bytes(np.full((SIZE, SIZE), SCALE, "<f2").tobytes())
        (evals / "m" / "on-real-val" / f"{name}.f16").write_bytes(tile(levels))
        injections.append({"Tile": t, "SessionId": sid, "CellX": 0, "CellY": 0})
        outputs.append({"Tile": t, "Output": f"{name}.f16"})
        stars = ["x,y,significance,outcome,second_pass,saturated"]
        stars += [f"{x},{y},500,Subtracted,0,0" for x, y in POSITIONS[:len(levels)]]
        stars += [f"{x},{y},{sig},Subtracted,0,0" for x, y, sig in faint]
        (plates / f"{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv").write_text("\n".join(stars) + "\n", encoding="utf-8")
    (export / "injections.jsonl").write_text("\n".join(map(json.dumps, injections)) + "\n", encoding="utf-8")
    (evals / "m" / "on-real-val" / "outputs.jsonl").write_text("\n".join(map(json.dumps, outputs)) + "\n", encoding="utf-8")
    return export, plates, evals


def block(lines, title):
    i = lines.index(title)
    rows = []
    for line in lines[i + 2:]:
        rows.append(" ".join(line.split()))
        if line.startswith("all "):
            break
    return rows


def main():
    with tempfile.TemporaryDirectory(prefix="real-star-sessions-") as tmp:
        export, plates, evals = build(Path(tmp))
        r = subprocess.run([sys.executable, "-I", str(HERE / "real_star_check.py"), "--export", str(export), "--plates", str(plates),
                            "--eval-root", str(evals), "input", "m"], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        return 1
    lines = r.stdout.splitlines()
    wrong = []

    def expect(what, got, want):
        if got != want:
            wrong.append(f"{what}: got {got!r}, want {want!r}")

    expect("the count line", lines[0], "3 real master tiles, 10 catalogued stars inside the rim 5-20 sigma: 1, 20-100 sigma: 1, 100-1000 sigma: 8")
    pooled = [" ".join(l.split()) for l in lines if l.lstrip().startswith("100-1000 sigma")]
    expect("the pooled bright rows", pooled, [
        "100-1000 sigma left |<1| 0.00 50.00",
        "100-1000 sigma light >+1 100.00 37.50",
        "100-1000 sigma dug <-1 0.00 12.50",
        "100-1000 sigma median 5.00 0.00"])
    for title, want in (
            ("per session, 100-1000 sigma, % flat |<1|", ["Lobster 4 / 1 0.0 / 0.0 50.0 / 0.0", "M42 (ASI294MC) 3 0.0 66.7", "all 8 0.0 50.0"]),
            ("per session, 100-1000 sigma, % left >+1", ["Lobster 4 / 1 100.0 / 100.0 50.0 / 0.0", "M42 (ASI294MC) 3 100.0 33.3", "all 8 100.0 37.5"]),
            ("per session, 100-1000 sigma, % dug <-1", ["Lobster 4 / 1 0.0 / 0.0 0.0 / 100.0", "M42 (ASI294MC) 3 0.0 0.0", "all 8 0.0 12.5"])):
        expect(title, block(lines, title) if title in lines else "missing", want)
    if wrong:
        print("\n".join(wrong) + "\n--- output ---\n" + r.stdout)
        return 1
    print("real_star_check per-session table: ok")
    return 0


sys.exit(main())
