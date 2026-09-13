"""Noise2Noise smoke run on the calgated archive: does a denoiser actually learn anything?

Reads the P0 tiles as stored (post-stretch [0,1], CHW fp16) and never re-implements
preprocessing, per DatasetTileExporter's zero-skew contract. Trains sub -> different sub of
the SAME cell, then asks the only question that matters: measured against the session
master (integrated from ~120 subs, so ~9x less noise), is a denoised sub closer than the
raw sub was, and does it beat a tuned Gaussian blur?

Staged so the slow part happens once:
  --prepare   pack the chosen sessions' tiles from the USB spindle into one NVMe memmap
  --train     train from that memmap
  --eval      score held-out sessions + write comparison PNGs

A pair of subs can only be made so quiet: 8 subs per cell caps the disjoint average at 4v4,
which measured 2.96x the master's own background noise, while the model is DEPLOYED on the
master itself. --half-pairs closes most of that by training on the exported half-masters, each
integrating an interleaved half of the session, measured at ~1.41x the master. Use n2n_depth.py
to re-measure rather than assuming 1/sqrt(n): the halves carry correlated residue that ideal
shot-noise scaling does not predict, and the sqrt(n) figure is the optimistic bound, not the
level.
"""
import argparse
import json
import os
import random
import struct
import time
from collections import defaultdict

import numpy as np

TILE, CH = 256, 3
BORDER = 16          # AiNafnetInputs.StitchBorderPx: no output pixel ever comes from a chunk edge
BYTES = CH * TILE * TILE * 2
SUBS_PER_CELL = 8
MIX_LEVELS = (1, 2, 4)   # subs averaged per side; 8 subs per cell caps the disjoint pair at 4
HALF = "half"            # the half-master regime, which is not a count of subs
SYNTH = "synth"          # supervised against slot 0, only meaningful on an injected cache

# Cache slot map. Subs occupy 1..SUBS_PER_CELL so a sub index doubles as its slot.
SLOT_MASTER = 0
SLOT_HALF_A = SUBS_PER_CELL + 1
SLOT_HALF_B = SUBS_PER_CELL + 2
SLOTS_SUBS_ONLY = SUBS_PER_CELL + 1     # what every cache before the half-master bake had
SLOTS_WITH_HALVES = SUBS_PER_CELL + 3


def open_tiles(cache, meta):
    """The tile memmap at whatever slot count this cache was written with.

    A cache predating the half-master slots carries no `slots` key, so it reads back as 9 and
    keeps working. Every script goes through here rather than restating the shape, which is
    what makes adding a slot a one-line change instead of a seven-file sweep.
    """
    return np.memmap(os.path.join(cache, "tiles.f16"), dtype=np.float16, mode="r",
                     shape=(meta["cells"], meta.get("slots", SLOTS_SUBS_ONLY), CH, TILE, TILE))


def open_cache(cache):
    """(mm, meta) in one call, for the scripts that want both."""
    meta = json.load(open(os.path.join(cache, "meta.json"), encoding="utf-8"))
    return open_tiles(cache, meta), meta


# --------------------------------------------------------------------------- index
def load_cells(root, manifest):
    """(session, cx, cy) -> {'subs': [relpath...], 'master': relpath, 'half_a'/'half_b': relpath}

    The two halves integrate disjoint runs of the same session (subs 1..n/2 and n/2+1..n), so
    they are an N2N pair whose noise sits at the level the model is actually DEPLOYED at,
    unlike a pair of subs. Absent for a session too shallow to halve.
    """
    cells = defaultdict(lambda: {"subs": [], "master": None, "half_a": None, "half_b": None, "night_c": None})
    with open(os.path.join(root, manifest), encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            d = json.loads(line)
            key = (d["SessionId"], d["CellX"], d["CellY"])
            frame = d["Frame"]
            if frame == "master":
                cells[key]["master"] = d["Tile"]
            elif frame == "halfmaster_a":
                cells[key]["half_a"] = d["Tile"]
            elif frame == "halfmaster_b":
                cells[key]["half_b"] = d["Tile"]
            elif frame == "nightc":
                # A triple export's measurement night. It sits on the pair's grid and is a THIRD
                # observation of the same sky, which is what separates a night's photon noise from its
                # systematic; it is not an input and not a target, and the `else` below would file it
                # as a sub slot, which is exactly the silent way to train on it.
                cells[key]["night_c"] = d["Tile"]
            else:
                cells[key]["subs"].append(d["Tile"])
    return cells


def has_halves(entry):
    return entry["half_a"] is not None and entry["half_b"] is not None


def drop_foreign_channel_sessions(root, cells):
    """Drop sessions whose tiles are not CH-channel, BEFORE a single tile is read.

    The dataset carries mono sessions (2 of 67, both ASI1600MM Pro) whose tiles are one channel
    against the OSC three. The 8-session smoke split never met one, so the trainer assumed 3
    everywhere; widening to 60 sessions hit one and prepare died 1100 cells in, five minutes of
    disk work after the decision that doomed it. One stat per session up front turns that into a
    line of output. The strict per-tile size check in prepare stays as the backstop, so a session
    with MIXED tile sizes is still caught rather than silently half-read.

    Excluding them rather than supporting them is deliberate for now: the conditioning plane, the
    band loss and the deployment target are all OSC, so a mono session is a different problem and
    folding it in silently would be a confound rather than more data.
    """
    by_session = defaultdict(list)
    for key in cells:
        by_session[key[0]].append(key)
    dropped = {}
    for session, keys in by_session.items():
        rel = cells[keys[0]]["master"]
        size = os.path.getsize(os.path.join(root, rel.replace("/", os.sep)))
        if size != BYTES:
            dropped[session] = (len(keys), size)
    if dropped:
        print(f"skipping {len(dropped)} session(s) whose tiles are not {CH}-channel:")
        for session, (n_cells, size) in sorted(dropped.items()):
            print(f"  {size // (TILE * TILE * 2)}ch  {n_cells:4d} cells  {session}")
        cells = {k: v for k, v in cells.items() if k[0] not in dropped}
    return cells


def read_val_names(meta_path):
    """The val session names recorded in an existing cache's meta.json, or None."""
    if not meta_path:
        return None
    with open(meta_path, "r", encoding="utf-8") as fh:
        return json.load(fh)["val_sessions"]


def read_val_list(path):
    """An explicit val session list.

    The companion to --train-from-list, and it exists for the same reason --train-from-list does:
    when sessions must be EXCLUDED (E2 excludes every session that shares a camera and target with
    an eval4 observer, or the arm is scored on scenes it trained on), the exclusion has to hold for
    both splits. Pinning val to a meta.json only copies whatever the first cache happened to pick,
    which is not an exclusion.
    """
    return read_name_list(path, "val")


def read_name_list(path, what):
    """An explicit session list: one name per line, blanks and # comments ignored."""
    if not path:
        return None
    names = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line and not line.startswith("#"):
                names.append(line)
    if not names:
        raise SystemExit(f"{what} session list {path} is empty")
    return names


def read_train_names(path):
    """An explicit train session list: one name per line, blanks and # comments ignored.

    Pinning TRAIN by name exists for subset experiments, and the obvious alternative does not
    work. Dropping N sessions and re-slicing `sessions[:count - N]` looks equivalent and is not:
    the shuffle runs over the WHOLE session list, so removing 8 entries pulls the next 8 up into
    the gap. The resulting arm would differ from the run it is compared against by 16 sessions
    while claiming to differ by 8, which is a confound built into the instrument.
    """
    return read_name_list(path, "train")


def choose(cells, n_train_sessions, n_val_sessions, cells_per_session, seed=42,
           require_halves=False, val_names=None, val_cells_per_session=None,
           train_names=None):
    by_session = defaultdict(list)
    for key, entry in cells.items():
        if require_halves and not has_halves(entry):
            continue
        by_session[key[0]].append(key)
    sessions = sorted(by_session)
    rng = random.Random(seed)
    rng.shuffle(sessions)
    if not val_names:
        # Original behaviour: val is whatever follows the train block, so it MOVES when the
        # train count changes.
        train_s = sessions[:n_train_sessions]
        val_s = sessions[n_train_sessions:n_train_sessions + n_val_sessions]
    else:
        # Pin val BY NAME, so growing the train set cannot swallow the held-out sessions and
        # every gate threshold stays calibrated against the session it was measured on.
        # By name rather than by index because the shuffle runs over the whole session list:
        # baking one extra session reorders all of it, so an index that reproduced a split
        # yesterday silently selects different sessions today. Names survive that.
        missing = [s for s in val_names if s not in by_session]
        if missing:
            raise SystemExit("val sessions not present in this root:\n  " + "\n  ".join(missing))
        val_s = list(val_names)
        train_s = [s for s in sessions if s not in set(val_s)][:n_train_sessions]
    if train_names:
        # Explicit train set. Overrides the count entirely; --train-sessions is ignored, and a
        # name that is not in this root is fatal rather than silently dropped, because a subset
        # arm that quietly trains on 51 of the 52 it names is not the arm anyone reasoned about.
        missing = [s for s in train_names if s not in by_session]
        if missing:
            raise SystemExit("train sessions not present in this root:\n  " + "\n  ".join(missing))
        overlap = sorted(set(train_names) & set(val_s))
        if overlap:
            raise SystemExit("train list contains held-out val session(s):\n  "
                             + "\n  ".join(overlap))
        train_s = list(train_names)
    val_n = cells_per_session if val_cells_per_session is None else val_cells_per_session

    def pick(session_list, per_session):
        out = []
        for s in session_list:
            keys = sorted(by_session[s])
            # Seeded per session name so the same cells are chosen on a re-run, independent
            # of how many sessions were requested.
            random.Random(f"{seed}:{s}").shuffle(keys)
            out.extend(keys[:per_session])
        return out

    return (pick(train_s, cells_per_session), pick(val_s, val_n), train_s, val_s)


# --------------------------------------------------------------------------- cache
def load_psf01(root):
    """psf01 per degraded tile, keyed by the tile's own relative path.

    Read from `degradations.jsonl`, which `tianwen dataset degrade` writes beside the tile manifest.
    Keyed on the PATH rather than on (session, cell, frame) because that tuple would have to be
    re-derived here and the path is already the join key both files agree on.

    Absent, null or star-less rows are dropped rather than defaulted. A missing label is not a zero:
    the exporter writes null exactly when its estimator found no stars and fell back to a constant
    radius, and training on that constant would condition the model on a number nothing measured.
    """
    path = os.path.join(root, "degradations.jsonl")
    if not os.path.exists(path):
        return {}

    out = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue

            row = json.loads(line)
            value = row.get("Psf01Estimated")
            if value is None or not np.isfinite(value):
                continue

            out[row["Tile"]] = float(value)
    return out


def load_kernel_rows(root):
    """The operator's kernel per degraded tile, keyed by tile path like `load_psf01`.

    Columns are `n2n_operator.KERNEL_COLUMNS`. `EstimatedKernelFwhmPx` / `EstimatedKernelBeta` are
    what the operator convolves with: the estimator step's reading where it returned, the drawn
    kernel's effective width and beta where it was refused (`KernelSource` "drawn"), which is the
    whole-frame fallback inference will use. `SourceEstimated` says which, so a run can report what
    fraction of its cells trained on a measured kernel. A row without the columns (an export before
    `--estimate-kernels`) is dropped, and the cache then carries no kernel file at all.
    """
    import n2n_operator as OP
    path = os.path.join(root, "degradations.jsonl")
    if not os.path.exists(path):
        return {}
    out = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            fwhm = row.get("EstimatedKernelFwhmPx")
            beta = row.get("EstimatedKernelBeta")
            if fwhm is None or beta is None:
                continue
            values = np.full(len(OP.KERNEL_COLUMNS), np.nan, dtype=np.float32)
            for j, col in enumerate(OP.KERNEL_COLUMNS):
                if col == "SourceEstimated":
                    values[j] = 1.0 if str(row.get("KernelSource", "")).lower() == "estimated" else 0.0
                else:
                    v = row.get(col)
                    if v is not None and np.isfinite(v):
                        values[j] = float(v)
            out[row["Tile"]] = values
    return out


def prepare_stretch(bake, keys, cells, read_tile):
    """Per-cell session stretch parameters `[cells, 6]`, each session's proved against its own tiles.

    See `n2n_operator`'s module docstring for why these exist: the operator deconvolves in linear
    units and the exporter recorded the stretch nowhere. Recomputed from the retained master in
    `bake`, and every session's parameters have to reproduce up to three of the cache's clean tiles to
    within two half-ulps before they are written; a session that does not refuses the whole prepare.
    Returns the array and a per-session record for `stretch.json`.
    """
    import n2n_operator as OP
    sessions = sorted({k[0] for k in keys}, key=str)
    per_session = {}
    for s in sessions:
        master, header, path = OP.read_master(bake, s)
        data_max = float(np.nanmax(master))
        candidates = [data_max if data_max > 1.0 else 1.0]
        if "DATAMAX" in header:
            try:
                dm = float(header["DATAMAX"])
                if dm > 1.0 and dm not in candidates:
                    candidates.append(dm)
            except (TypeError, ValueError):
                pass
        proof_cells = [(int(k[1]), int(k[2]), cells[k]["master"]) for k in keys if k[0] == s]
        divisor, mins, betas, worst_max, worst_med = OP.prove_stretch_params(
            master, header, candidates, lambda d: OP.session_stretch_params(master, d),
            proof_cells, read_tile, TILE)
        per_session[s] = {"master": path, "divisor": divisor, "min": [float(v) for v in mins],
                          "beta": [float(v) for v in betas], "parity_max_abs": worst_max,
                          "parity_median_abs": worst_med, "proof_cells": min(3, len(proof_cells))}
        print(f"  stretch {s[:60]}: divisor {divisor:.6g} beta ({betas[0]:.4f}, {betas[1]:.4f}, {betas[2]:.4f}) "
              f"min ({mins[0]:.5f}, {mins[1]:.5f}, {mins[2]:.5f}) parity max {worst_max:.2e} med {worst_med:.2e}")
    out = np.zeros((len(keys), len(OP.STRETCH_COLUMNS)), dtype=np.float32)
    for i, k in enumerate(keys):
        rec = per_session[k[0]]
        out[i, :3] = rec["min"]
        out[i, 3:] = rec["beta"]
    return out, per_session


def prepare(args):
    cells = load_cells(args.root, args.manifest)
    cells = drop_foreign_channel_sessions(args.root, cells)
    train_keys, val_keys, train_s, val_s = choose(
        cells, args.train_sessions, args.val_sessions, args.cells_per_session,
        require_halves=args.require_halves,
        val_names=read_val_list(args.val_from_list) or read_val_names(args.val_from_meta),
        val_cells_per_session=args.val_cells_per_session,
        train_names=read_train_names(args.train_from_list))
    keys = train_keys + val_keys
    print(f"sessions: {len(train_s)} train / {len(val_s)} val; cells: "
          f"{len(train_keys)} train / {len(val_keys)} val")
    if args.require_halves:
        print("  (restricted to sessions carrying a half-master pair)")

    os.makedirs(args.cache, exist_ok=True)
    n = len(keys)
    # [cell, 11, C, H, W]: slot 0 master, 1..8 subs, 9 half_a, 10 half_b. The half slots are
    # written for every cell that has them even when nothing will train on them, so one cache
    # serves the control run and the half-pair run and the two cannot diverge on their tiles.
    path = os.path.join(args.cache, "tiles.f16")
    mm = np.memmap(path, dtype=np.float16, mode="w+",
                   shape=(n, SLOTS_WITH_HALVES, CH, TILE, TILE))
    halves = []
    # The deconvolver's conditioning label, per (cell, sub slot). NaN means "no label", which is a
    # state the trainer has to see rather than a zero it would silently condition on.
    psf01_by_tile = load_psf01(args.root)
    psf01 = np.full((n, SUBS_PER_CELL), np.nan, dtype=np.float32)
    # The operator's kernel per (cell, sub slot), from the same rows. NaN where a tile has no row.
    kernel_by_tile = load_kernel_rows(args.root)
    kernels = np.full((n, SUBS_PER_CELL, 6), np.nan, dtype=np.float32)

    def read_tile(rel):
        with open(os.path.join(args.root, rel.replace("/", os.sep)), "rb") as fh:
            raw = fh.read()
        if len(raw) != BYTES:
            raise SystemExit(f"tile {rel} is {len(raw)} bytes, expected {BYTES}")
        return np.frombuffer(raw, "<f2").reshape(CH, TILE, TILE)

    # The E3 operator's session stretch parameters, BEFORE the tile read: the proof against the cache's
    # clean tiles reads them straight from the export, so a parameter set that fails costs seconds
    # here rather than the fifteen minutes of tile packing it would otherwise sit behind.
    stretch = None
    stretch_info = None
    if args.bake:
        print("  session stretch parameters, proved against the export's clean tiles:")
        stretch, stretch_info = prepare_stretch(args.bake, keys, cells, read_tile)
    else:
        print("  session stretch parameters: NOT written (no --bake); the operator cannot run on this cache")

    t0 = time.perf_counter()
    for i, key in enumerate(keys):
        entry = cells[key]
        paths = [entry["master"]] + sorted(entry["subs"])[:SUBS_PER_CELL]
        for slot, rel in enumerate(paths):
            mm[i, slot] = read_tile(rel)
            if slot > 0 and rel in psf01_by_tile:
                psf01[i, slot - 1] = psf01_by_tile[rel]
            if slot > 0 and rel in kernel_by_tile:
                kernels[i, slot - 1] = kernel_by_tile[rel]
        pair = has_halves(entry)
        halves.append(pair)
        if pair:
            mm[i, SLOT_HALF_A] = read_tile(entry["half_a"])
            mm[i, SLOT_HALF_B] = read_tile(entry["half_b"])
        if (i + 1) % 100 == 0:
            done = i + 1
            rate = done / (time.perf_counter() - t0)
            print(f"  {done}/{n} cells  {rate:5.1f} cells/s  "
                  f"eta {(n - done) / rate / 60:5.1f} min", flush=True)
    mm.flush()

    print(f"  half-master pairs: {sum(halves)}/{n} cells")
    # Whether the sub slots hold INJECTED draws (frame names deg000..) rather than real subs. The
    # trainer needs to know, because --synthetic pairs a slot against slot 0 and that is only a
    # supervised pair when slot 0 is the clean target of the degradation rather than an integration
    # the subs are noisy views OF. Recorded from the tiles that were actually read, not from a flag,
    # so a cache cannot claim to be something its bytes are not.
    # A cross-night pair cache (tianwen dataset pair) has NO sub tiles: its two nights sit in the half
    # slots and the sub slots stay zero. `all()` over nothing is True, so without this a pair cache
    # would read as injected and --synthetic would regress zeros onto the master.
    has_subs = any(cells[key]["subs"] for key in keys)
    injected = has_subs and all(
        os.path.basename(rel).rsplit("_", 1)[-1].startswith("deg")
        for key in keys for rel in cells[key]["subs"][:SUBS_PER_CELL])
    print(f"  sub slots: {'INJECTED draws' if injected else 'real subs' if has_subs else 'EMPTY (a pair cache: train with --half-only)'}")

    labelled = int(np.isfinite(psf01).sum())
    if labelled:
        np.save(os.path.join(args.cache, "psf01.npy"), psf01)
        finite = psf01[np.isfinite(psf01)]
        print(f"  psf01 labels: {labelled}/{psf01.size} degraded slots, "
              f"p5 {np.quantile(finite, 0.05):.3f} p50 {np.quantile(finite, 0.5):.3f} "
              f"p95 {np.quantile(finite, 0.95):.3f}")
    else:
        # Said out loud rather than left to be discovered at train time, because a deconvolution arm
        # launched against an unlabelled cache would fall back to the noise plane and train something
        # nobody asked for.
        print("  psf01 labels: NONE (no degradations.jsonl, or no row carried a measured label)")

    # E3's operator labels. The kernel file is written whenever the export carried the columns; the
    # stretch file needs the bake the masters live in (--bake) and is PROVED against the tiles first.
    import n2n_operator as OP
    kernel_labelled = int(np.isfinite(kernels[..., 0]).sum())
    if kernel_labelled:
        np.save(os.path.join(args.cache, OP.KERNELS_FILE), kernels)
        with open(os.path.join(args.cache, "kernels.json"), "w", encoding="utf-8") as fh:
            json.dump({"columns": list(OP.KERNEL_COLUMNS), "slots": "sub slots 1..8 as index 0..7"}, fh, indent=1)
        est = kernels[..., 3][np.isfinite(kernels[..., 3])]
        fw = kernels[..., 0][np.isfinite(kernels[..., 0])]
        print(f"  operator kernels: {kernel_labelled}/{kernels[..., 0].size} degraded slots, "
              f"{est.mean() * 100:.0f}% estimated (the rest the drawn kernel's effective width), "
              f"fwhm p5 {np.quantile(fw, 0.05):.2f} p50 {np.quantile(fw, 0.5):.2f} p95 {np.quantile(fw, 0.95):.2f} px")
    else:
        print("  operator kernels: NONE (the export predates --estimate-kernels)")
    if stretch is not None:
        np.save(os.path.join(args.cache, OP.STRETCH_FILE), stretch)
        with open(os.path.join(args.cache, "stretch.json"), "w", encoding="utf-8") as fh:
            json.dump({"columns": list(OP.STRETCH_COLUMNS), "target_median": OP.TARGET_MEDIAN,
                       "bake": args.bake, "sessions": stretch_info}, fh, indent=1)

    meta = {
        "cells": n, "slots": SLOTS_WITH_HALVES, "injected": bool(injected), "has_subs": bool(has_subs),
        "train_cells": len(train_keys), "val_cells": len(val_keys),
        "train_sessions": train_s, "val_sessions": val_s,
        "has_halves": halves,
        "psf01_labels": labelled,
        "kernel_labels": kernel_labelled,
        "bake": args.bake,
        "stretch_proved": bool(stretch_info),
        "keys": [[k[0], k[1], k[2]] for k in keys],
    }
    with open(os.path.join(args.cache, "meta.json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh, indent=1)
    gb = os.path.getsize(path) / 2**30
    print(f"cached {n} cells ({gb:.2f} GiB) in {(time.perf_counter()-t0)/60:.1f} min -> {path}")
    prepare_stars(args.cache, args.star_max)


STARS_FILE = "stars.npy"
EMPTY_FILE = "empty.npy"


def prepare_stars(cache, max_per_tile=32):
    """Write `stars.npy` beside the tiles: each cell's CLEAN target (slot 0) run through the gate's own
    detector, so the star-term loss (--star-loss) and the gate see the same stars.

    A separate stage from the tile packing, and re-runnable on an EXISTING cache (--prepare-stars),
    because E2.8 has to train on the very cache E2.7 trained on: re-preparing would re-read 1.9 GiB
    of tiles from the export and put an assumption ("the bytes came out the same") between the arm
    and its paired control. This touches nothing but the new file.

    Layout: float32 [cells, max_per_tile, 4] of (y, x, peak over median in MAD, valid), in CROPPED
    coordinates (BORDER removed), which is the frame the loss and the gate both work in. The sidecar
    `stars.json` records the detector bar and the selection rule so a later reader need not guess.
    """
    import n2n_deconv_gate as DG           # imported here: it imports this module back
    import n2n_metrics as M
    mm, meta = open_cache(cache)
    n = meta["cells"]
    out = np.zeros((n, max_per_tile, len(DG.STAR_TARGET_COLUMNS)), dtype=np.float32)
    # The counterpart: as many EMPTY windows a tile as it has star targets (E2.8b arm N). Written
    # beside the stars from the same pass, so the two files can never describe different tiles.
    empty = np.zeros_like(out)
    counts = []
    empties = []
    t0 = time.perf_counter()
    for i in range(n):
        t = crop(np.asarray(mm[i, SLOT_MASTER], dtype=np.float32)).mean(axis=0)
        med = float(np.median(t))
        _, mad = M.bg_stats(t)
        out[i] = DG.star_targets(t, med, mad, max_per_tile)
        counts.append(int(out[i, :, 3].sum()))
        empty[i] = DG.empty_targets(t, med, mad, counts[-1], max_per_tile, seed=i)
        empties.append(int(empty[i, :, 3].sum()))
    np.save(os.path.join(cache, STARS_FILE), out)
    np.save(os.path.join(cache, EMPTY_FILE), empty)
    with open(os.path.join(cache, "stars.json"), "w", encoding="utf-8") as fh:
        json.dump({"columns": list(DG.STAR_TARGET_COLUMNS), "sigma": DG.STAR_SIGMA,
                   "max_per_tile": max_per_tile, "coordinates": f"cropped by BORDER={BORDER}",
                   "selection": "evenly spaced in peak rank when a tile exceeds max_per_tile",
                   "cells": n, "cells_with_stars": int(sum(c > 0 for c in counts)),
                   "empty_file": EMPTY_FILE, "empty_sigma": DG.STAR_SIGMA_LOW,
                   "empty_rule": "no low-bar detection within the 7x7 window plus one pixel, window max under the low bar, seeded per cell"},
                  fh, indent=1)
    counts = np.array(counts)
    empties = np.array(empties)
    print(f"star targets: {n} cells, stars/tile p10 {np.percentile(counts, 10):.0f} "
          f"p50 {np.median(counts):.0f} p90 {np.percentile(counts, 90):.0f}, "
          f"{int((counts == 0).sum())} cells with none, {int((counts == max_per_tile).sum())} at the "
          f"{max_per_tile} cap, in {time.perf_counter() - t0:.0f} s -> {os.path.join(cache, STARS_FILE)}")
    print(f"empty targets: {int(empties.sum())} windows over {n} cells (wanted {int(counts.sum())}), "
          f"{int((empties < counts).sum())} cells short of their star count -> {os.path.join(cache, EMPTY_FILE)}")


# --------------------------------------------------------------------------- model
def build_model(base, upsample=False, cond=0):
    """cond is a PLANE COUNT, not a flag: 0 off, 1 the scalar background sigma, COND_BANDS the
    per-band profile. It reads as a bool at every call site that only asks "is conditioning on",
    and `int(True) == 1`, so checkpoints written when it really was a bool still load correctly."""
    import torch.nn as nn

    def block(cin, cout):
        return nn.Sequential(
            nn.Conv2d(cin, cout, 3, padding=1), nn.LeakyReLU(0.1, inplace=True),
            nn.Conv2d(cout, cout, 3, padding=1), nn.LeakyReLU(0.1, inplace=True))

    import torch

    class UNet(nn.Module):
        """Deliberately small. The question here is whether the DATA supports N2N, not
        whether a big net wins; a 4 M-param net that cannot denoise means the pairing is
        wrong, and that is worth knowing before renting anything.

        With cond=1 the input carries a 4th plane holding the tile's own measured
        background sigma, so denoising strength is an INPUT rather than a constant baked in
        at training time. Without it the model assumes forever the noise level it was trained
        on, which is exactly why a sub-trained net over-cleans a master.

        With cond=COND_BANDS the single plane becomes a per-band profile, because a scalar
        cannot express noise SHAPE and the shape here is not a detail: measured scene-free, all
        three sub-derived regimes share one shape (band1/band0 = 0.601/0.596/0.589) while a
        half-master reads 0.320. So sub-averaging only ever closed the LEVEL half of the
        deployment gap, and one number labels two genuinely different distributions."""

        def __init__(self):
            super().__init__()
            cin = CH + int(cond)
            self.e1, self.e2, self.e3 = block(cin, base), block(base, base*2), block(base*2, base*4)
            self.mid = block(base*4, base*4)
            # ConvTranspose(k=2,s=2) is the textbook checkerboard generator: the kernel does
            # not evenly tile the stride, so output pixels get uneven numbers of contributions
            # and flat areas mottle. Nearest-upsample + 3x3 conv has no such asymmetry.
            def up(cin, cout):
                if upsample:
                    return nn.Sequential(nn.Upsample(scale_factor=2, mode="nearest"),
                                         nn.Conv2d(cin, cout, 3, padding=1))
                return nn.ConvTranspose2d(cin, cout, 2, stride=2)
            self.u3 = up(base*4, base*2)
            self.d3 = block(base*4, base*2)
            self.u2 = up(base*2, base)
            self.d2 = block(base*2, base)
            self.out = nn.Conv2d(base, CH, 1)
            self.pool = nn.MaxPool2d(2)

        def forward(self, x):
            e1 = self.e1(x)
            e2 = self.e2(self.pool(e1))
            e3 = self.e3(self.pool(e2))
            m = self.mid(e3)
            d3 = self.d3(torch.cat([self.u3(m), e2], 1))
            d2 = self.d2(torch.cat([self.u2(d3), e1], 1))
            # Residual: the net predicts the CORRECTION, so an untrained net is the identity
            # rather than noise, which makes "did it help" readable from step one. The
            # conditioning plane is an input only, so the residual adds to the IMAGE channels.
            return x[:, :CH] + self.out(d2)

    return UNet()


# --------------------------------------------------------------------- noise conditioning
def bg_sigma_torch(t):
    """Per-sample background sigma: MAD of the darkest half of the luminance.

    Every pixel in the darkest half sits below the median, so |v - med| there is med - v and
    its median is exactly the 25th percentile measured down from the median. That closed form
    avoids a masked median on the GPU and matches the numpy estimator the metrics use.
    """
    b = t.shape[0]
    flat = t[:, :CH].mean(dim=1).reshape(b, -1).float()
    med = flat.quantile(0.5, dim=1, keepdim=True)
    return (med - flat.quantile(0.25, dim=1, keepdim=True)).view(b, 1, 1, 1)


SIGMA_SCALE = 100.0  # a single sub sits near 0.01, so this puts the plane around 1.0

# Difference-of-Gaussian band edges in sigma-pixels, covering roughly 2-4, 4-8 and 8-16 px
# wavelengths. Every band is a BANDPASS with zero DC, so none can pick up the smooth scene.
COND_BAND_SIGMAS = ((0.0, 1.0), (1.0, 2.0), (2.0, 4.0))
COND_BANDS = len(COND_BAND_SIGMAS)
# Measured (n2n_bandprobe.py, 160 val cells) so a single sub lands near 1.0 in every plane.
COND_BAND_SCALES = (167.4, 267.6, 389.5)
COND_SCENE_SIGMA = 8.0   # coarse low-pass standing in for "how bright is the scene here"
COND_FAINT_FRAC = 0.25   # measure each band only over the faintest quarter


def band_sigma_torch(t):
    """Per-sample, per-band robust noise sigma. Returns [N, COND_BANDS, 1, 1].

    Two things this must get right, and the naive version gets both wrong:

    A band-passed image is NOT scene-free. Band-passing removes only the smooth component, and
    nebulosity plus star wings sit squarely in the 1-4 px bands, so a whole-tile MAD per band
    reads scene as noise. Restricting to the faintest quarter by a coarse low-pass is the same
    trick the scalar estimator's darkest-half does, applied per band, and it matters: against the
    scene-free truth it recovers 87% of the sub-to-half shape movement in band1 and 62% in band2,
    where the unmasked version manages 72% and only 31%.

    And it has to be computable from ONE image, because that is all inference ever has. The
    scene-free measurement needs two independent views of the same scene and exists only as the
    yardstick this estimator is calibrated against.
    """
    import torch
    dev = t.device
    ks = {s: _gauss_kernel(s, dev) for pair in COND_BAND_SIGMAS for s in pair if s > 0}
    ks[COND_SCENE_SIGMA] = _gauss_kernel(COND_SCENE_SIGMA, dev)

    lum = t[:, :CH].mean(dim=1, keepdim=True).float()
    scene = _blur(lum, ks[COND_SCENE_SIGMA]).flatten(1)
    keep = scene <= scene.quantile(COND_FAINT_FRAC, dim=1, keepdim=True)

    def masked_median(v, keepdim=False):
        # Excluded pixels pushed to +inf, so the kept values are exactly the lowest
        # COND_FAINT_FRAC of each sorted row and their median sits at quantile FRAC/2.
        # A ragged gather has no batched form; this does the same thing in one kernel.
        pushed = torch.where(keep, v, torch.full_like(v, float("inf")))
        return torch.quantile(pushed, COND_FAINT_FRAC / 2, dim=1, keepdim=keepdim)

    out = []
    for lo, hi in COND_BAND_SIGMAS:
        a = lum if lo == 0 else _blur(lum, ks[lo])
        band = (a - _blur(lum, ks[hi])).flatten(1)
        out.append(masked_median((band - masked_median(band, keepdim=True)).abs()))
    return torch.stack(out, dim=1).view(-1, COND_BANDS, 1, 1)


def with_sigma(x, strength=1.0, planes=1):
    """Append the conditioning plane(s) to an image batch.

    `strength` deliberately LIES to the model about how noisy its input is. Because denoising
    strength is an input rather than a constant learned at training time, overstating sigma is
    a free strength dial at inference with no retraining: 1.0 is honest, >1 denoises harder,
    <1 gentler. The catch is that it walks the model away from the conditioning it was trained
    on, so it has to be measured rather than assumed monotone-and-safe.

    With planes=COND_BANDS the dial becomes per-band for free, which is the per-frequency
    strength control the plan had deferred: `strength` may be a scalar or a COND_BANDS-long
    sequence. Nothing user-facing is required for it to exist.
    """
    import torch
    if planes == COND_BANDS:
        s = band_sigma_torch(x) * torch.tensor(
            COND_BAND_SCALES, device=x.device, dtype=torch.float32).view(1, COND_BANDS, 1, 1)
        if not isinstance(strength, (int, float)):
            strength = torch.tensor(list(strength), device=x.device,
                                    dtype=torch.float32).view(1, COND_BANDS, 1, 1)
        s = s * strength
    else:
        s = bg_sigma_torch(x) * SIGMA_SCALE * strength
    return torch.cat([x, s.expand(-1, -1, x.shape[2], x.shape[3])], dim=1)


def load_model(cache, name, dev):
    """One loader for every eval script, so a checkpoint flag can never be read two ways.

    Returns the conditioning PLANE COUNT, which is falsy when off. Checkpoints predating the
    band profile stored `cond` as a bool and `int(True) == 1` is the scalar plane, so they load
    unchanged and no migration is needed.
    """
    import torch
    ck = torch.load(os.path.join(cache, name), map_location="cpu")
    planes = int(ck.get("cond", 0))
    if ck.get("operator") == "rl":
        # An E3.1 checkpoint: the operator around its prior. `planes` stays the label-plane count the
        # gate appends (1), which is what every caller uses it for; the prior itself takes no planes.
        import n2n_operator as OP
        prior = OP.StretchedPrior(build_model(ck["prior_base"], ck.get("upsample", False), 0),
                                  every=ck.get("prior_every", 1))
        model = OP.RLOperator(ck["rl_k"], prior=prior).to(dev)
    else:
        model = build_model(ck["base"], ck.get("upsample", False), planes).to(dev)
    model.load_state_dict(ck["model"])
    model.eval()
    return model, planes


def denoise(cache, name, src, dev, batch=16, strength=1.0):
    """Run a checkpoint over an [N,C,H,W] float32 array, honouring its conditioning flag."""
    import torch
    model, planes = load_model(cache, name, dev)
    if not planes and strength != 1.0:
        raise ValueError(f"{name} is not conditioned, so strength has nothing to act on")
    out = []
    with torch.no_grad():
        for i in range(0, len(src), batch):
            x = torch.from_numpy(src[i:i + batch]).to(dev)
            out.append(model(with_sigma(x, strength, planes) if planes else x).cpu().numpy())
    return np.concatenate(out)


# --------------------------------------------------------------------------- train
def _gauss_kernel(sigma, dev):
    import torch
    r = max(1, int(3 * sigma))
    x = torch.arange(-r, r + 1, device=dev, dtype=torch.float32)
    k = torch.exp(-(x ** 2) / (2 * sigma ** 2))
    return (k / k.sum()).view(1, 1, -1)


def _blur(t, k):
    import torch.nn.functional as F
    c = t.shape[1]
    r = k.shape[-1] // 2
    kh = k.expand(c, 1, 1, k.shape[-1])
    kv = k.view(1, 1, -1, 1).expand(c, 1, k.shape[-1], 1)
    t = F.conv2d(F.pad(t, (r, r, 0, 0), mode="reflect"), kh, groups=c)
    return F.conv2d(F.pad(t, (0, 0, r, r), mode="reflect"), kv, groups=c)


# The star-term loss's window: 7x7 around each detected star, an aperture of radius 3 (29 px) and a
# core of radius 1.5 (the 3x3 block, 9 px). The core is a block rather than a 5-px plus so a peak that
# lands half a pixel off the detected maximum still sits inside it.
STAR_WINDOW_R = 3
STAR_APERTURE_R = 3.0
STAR_CORE_R = 1.5


class StarTerm:
    """E2.8 / H10: a loss that counts STARS, because L2 counts pixels.

    Pixel-wise L2, banded or not, weights error by amplitude squared times pixel count, and a faint
    star is ten pixels at a few sigma: about 1e-4 of a tile's loss, so suppressing it is free. E2.7
    measured that arithmetic working (width 1.328 with 0.54 of the truth's stars against a 0.62
    floor). This term gives every detected star EQUAL weight regardless of its brightness or size, in
    three ratios that are each dimensionless and each about the star rather than the background:

      |log flux_out / flux_target|   over the aperture (r <= 3), so the star keeps its light;
      |log peak_out / peak_target|   the brightest core pixel, so it keeps its height;
      |log conc_out / conc_target|   concentration = core energy over aperture energy, which RISES
                                     when a star tightens, so it must tighten exactly as much as the
                                     target did and no more.

    Everything is background-subtracted against the TARGET tile's median and clamped at zero, with one
    MAD of the target's darkest half added inside every log so a star the output has erased reads a
    finite, large penalty instead of an infinite one. The luminance (channel mean) is used, which is
    what the gate measures on.

    The weight is set ONCE, so the term equals the L2 term on the first batch that carries a star, and
    is then fixed and logged. Never tuned on the gate: a weight chosen by watching the selection metric
    would be a second selection on the same held-out session.
    """

    def __init__(self, stars, device, empties=None):
        import torch
        self.stars = torch.as_tensor(stars, device=device, dtype=torch.float32)  # [cells, S, 4]
        # E2.8b arm N: the same ratios over windows where the target is EMPTY, concatenated onto the
        # star set so each empty window weighs exactly what a star does. Raising a peak over empty sky
        # then costs what lowering a star's peak costs, which E2.8's term never charged for.
        if empties is not None:
            self.stars = torch.cat([self.stars, torch.as_tensor(empties, device=device, dtype=torch.float32)], dim=1)
        off = torch.arange(-STAR_WINDOW_R, STAR_WINDOW_R + 1, device=device)
        self.oy = off.view(1, 1, -1, 1)
        self.ox = off.view(1, 1, 1, -1)
        dist = torch.sqrt((off.view(-1, 1) ** 2 + off.view(1, -1) ** 2).float())
        self.aperture = (dist <= STAR_APERTURE_R).float()
        self.core = (dist <= STAR_CORE_R).float()
        self.n_aperture = float(self.aperture.sum())
        self.n_core = float(self.core.sum())

    def __call__(self, pc, yc, idx):
        """Mean of the three-ratio penalty over every valid star in the batch, and the star count.

        pc, yc: [B, C, H, W] CROPPED prediction and target; idx: the batch's cache cell indices.
        Returns a zero (still attached) when the batch carries no star, and 0 as the count.
        """
        import torch
        st = self.stars[torch.as_tensor(idx, device=self.stars.device)]  # [B, S, 4]
        valid = st[..., 3] > 0
        n = int(valid.sum())
        if n == 0:
            return pc.sum() * 0.0, 0

        lo = pc.mean(1)      # [B, H, W]
        lt = yc.mean(1)
        flat = lt.flatten(1)
        med = flat.median(dim=1).values                                   # [B]
        mad = (med - flat.quantile(0.25, dim=1)).clamp_min(1e-6)          # bg_sigma_torch's closed form
        ys = st[..., 0].long()
        xs = st[..., 1].long()
        yy = ys[..., None, None] + self.oy                                 # [B, S, 7, 7]
        xx = xs[..., None, None] + self.ox
        b = torch.arange(pc.shape[0], device=pc.device).view(-1, 1, 1, 1)
        so = (lo[b, yy, xx] - med.view(-1, 1, 1, 1)).clamp_min(0)
        stt = (lt[b, yy, xx] - med.view(-1, 1, 1, 1)).clamp_min(0)
        eps = mad.view(-1, 1)                                              # one MAD per pixel

        flux_o = (so * self.aperture).sum((-1, -2)) + eps * self.n_aperture
        flux_t = (stt * self.aperture).sum((-1, -2)) + eps * self.n_aperture
        peak_o = (so * self.core).amax((-1, -2)) + eps
        peak_t = (stt * self.core).amax((-1, -2)) + eps
        core_o = (so * self.core).sum((-1, -2)) + eps * self.n_core
        core_t = (stt * self.core).sum((-1, -2)) + eps * self.n_core

        per_star = ((torch.log(flux_o) - torch.log(flux_t)).abs()
                    + (torch.log(peak_o) - torch.log(peak_t)).abs()
                    + (torch.log(core_o / flux_o) - torch.log(core_t / flux_t)).abs())
        return (per_star * valid).sum() / n, n


def gate_cells(meta, n_sessions, limit):
    """Val cells belonging to the FIRST n_sessions val sessions, for the mid-training probe.

    Deliberately a subset of the val sessions rather than all of them: selecting a checkpoint on
    a measurement spends the held-out-ness of whatever it was measured on, so the remaining val
    session stays clean for the report. Gating on training cells would keep val pristine but
    measure tiles the model has already fitted.
    """
    wanted = set(meta["val_sessions"][:n_sessions])
    keys, n_train = meta["keys"], meta["train_cells"]
    picked = [i for i in range(n_train, meta["cells"]) if keys[i][0] in wanted]
    return _thin(picked, limit)


def _thin(picked, limit):
    """Evenly spaced rather than the first N, so one corner of one panel cannot stand for a slice."""
    if limit and len(picked) > limit:
        step = len(picked) / limit
        picked = [picked[int(j * step)] for j in range(limit)]
    return picked


def observer_cells(meta, n_selecting, limit):
    """Per-session cell lists for the val sessions the gate does NOT select on.

    These exist to make a session-STABILITY question answerable: `spurious_over_floor` does not
    transfer between sessions, so a fixed threshold means different things depending on which one a
    run happened to probe, and the proposed remedy is a relative stopping rule on the metric whose
    ORDERING does transfer (`log_ratio`). Testing that needs the same run's trajectory measured on
    two sessions at once, which no run has ever produced.

    Observing costs no held-out-ness, because held-out-ness is spent by SELECTING on a measurement,
    not by taking it. Selection stays on the first session exactly as before, so these runs remain
    comparable to the earlier ones, and the extra trajectory is recorded and never acted on.
    """
    keys, n_train = meta["keys"], meta["train_cells"]
    out = []
    for s in meta["val_sessions"][n_selecting:]:
        cells = [i for i in range(n_train, meta["cells"]) if keys[i][0] == s]
        if cells:
            out.append((s, _thin(cells, limit)))
    return out


def train(args):
    import torch
    import torch.nn as nn

    mm, meta = open_cache(args.cache)
    n = meta["cells"]
    if args.half_only:
        # A cross-night pair cache: the two nights are the half slots and the sub slots are empty,
        # so the half regime is the only one with pixels in it. Implied here, before the RAM load
        # below decides on it, and the regime list is emptied further down.
        args.half_pairs = True
    if args.pair_avg > 1 or args.mix_avg or args.half_pairs:
        # Averaging K subs per side multiplies the per-sample reads by K, and fancy-indexing a
        # memmap per sample is far slower than the GPU step it feeds, so the whole cache is
        # resident. It stays float16 here (asarray on an f16 memmap does not widen), which is
        # what keeps it affordable: the v17 cache is 2940 cells = 11.8 GiB, and a f32 copy would
        # be 23.7 GiB and would not fit. Sizing a cache is therefore bounded by RAM, not disk --
        # cells x 11 x 3 x 256 x 256 x 2 bytes, or 4.33 MiB per cell.
        print("loading tiles into RAM for the averaging path ...", flush=True)
        mm = np.asarray(mm)
    n_train = meta["train_cells"]

    # Which regimes one model sees. A half-master pair is not K subs averaged: it integrates an
    # interleaved HALF of the session, measured at ~1.41x the master's own background noise
    # against 2.96x for the deepest pair 8 subs allow (4v4). That is the regime the model is
    # deployed in and, until this bake, the training set had no pair anywhere near it.
    regimes = list(MIX_LEVELS) if args.mix_avg else [args.pair_avg]
    if args.half_only:
        regimes = []            # HALF is appended below; nothing else has pixels on this cache
    elif not meta.get("has_subs", True):
        raise SystemExit("this cache has no sub tiles (a cross-night pair cache from "
                         "`tianwen dataset pair`); train it with --half-only")
    if args.synthetic:
        # Supervised, and EXCLUSIVE: an arm that mixed noise-to-clean with noise-to-noise would not
        # answer H1, which asks whether supervised injection beats N2N at deployment depth. Refused
        # on a cache of real subs, where slot 0 is an integration the subs are noisy views of and
        # "supervised" would silently mean "regress a sub onto a 9x quieter version of itself".
        if not meta.get("injected"):
            raise SystemExit("--synthetic needs a cache prepared from an injected export "
                             "(tianwen dataset degrade); this one holds real subs")
        if args.half_pairs:
            raise SystemExit("--synthetic and --half-pairs are different regimes; pick one")
        regimes = [SYNTH]
        if args.synthetic_target == "master":
            print("regime: synthetic (a degraded draw against the clean target in slot 0). "
                  "Note the target is a MASTER, so it carries its own 1/sqrt(N) noise and the model "
                  "learns to leave that; score against a held-out half, never against this target.")
        else:
            print(f"regime: synthetic (a degraded draw against {args.synthetic_target}). Note the target "
                  "is a MASTER, so it carries its own 1/sqrt(N) noise and the model learns to leave that; "
                  "score against a held-out half, never against this target.")
    # Which frame the supervised regime regresses onto. On a pair cache the two half slots are two
    # NIGHTS of one sky, so half-b is a target whose noise is independent of the input's by
    # construction, and half-a is the same night the input was degraded from: the same inputs, the
    # same cells, and the only difference is whether the target shares the input's noise. That is the
    # one axis H8 is about, and every earlier cross-night arm varied it together with the input
    # distribution, which is why its kill said nothing about the mechanism.
    synth_target = {"master": SLOT_MASTER, "half-a": SLOT_HALF_A, "half-b": SLOT_HALF_B}[args.synthetic_target]
    if args.synthetic and args.synthetic_target != "master":
        print(f"  supervised target: slot {synth_target} ({args.synthetic_target}), not the combined master")
    half_train = np.array([], dtype=np.int64)
    if args.half_pairs:
        flags = meta.get("has_halves")
        if flags is None:
            raise SystemExit("this cache predates the half-master slots; re-run --prepare")
        half_train = np.flatnonzero(np.asarray(flags[:n_train], dtype=bool))
        if half_train.size == 0:
            raise SystemExit("--half-pairs but no training cell carries a pair; "
                             "prepare with --require-halves")
        regimes.append(HALF)
        print(f"regimes {regimes}; {half_train.size}/{n_train} train cells carry a pair")

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    # Seed torch BEFORE the model is built, because the weights are the thing this fixes.
    #
    # This was missing for the whole smoke series and it cost a wrong conclusion. The numpy
    # stream was seeded (so two runs drew the same tiles in the same order, and their per-regime
    # step counts matched exactly, which is what made the runs LOOK controlled) while the weight
    # initialisation came from torch's unseeded default generator. So every run started somewhere
    # different, and two runs of one config diverged as much on the selection metrics as two
    # different configs did: v9h and v9g_final are the same config and read 0.038 vs 0.273
    # residual correlation. Any A/B without this is measuring initialisation.
    torch.manual_seed(args.seed)
    # Seeding alone left ~7e-7 of drift between two runs of one seed, from cudnn picking
    # different (equally valid) kernels. That is tiny next to the 0.31 the unseeded init caused,
    # but it compounds over 4000 Adam steps, so pin the kernels too and let an A/B be exact.
    # benchmark=False because autotuning is what chooses the varying kernel in the first place.
    if not args.nondeterministic:
        torch.backends.cudnn.deterministic = True
        torch.backends.cudnn.benchmark = False
    # One resolved plane count from here down, so the model, the training step, the gate and the
    # checkpoint cannot disagree about what the input looks like.
    cond_planes = COND_BANDS if args.cond_bands else (1 if args.cond else 0)

    # A DECONVOLUTION arm conditions on a stored psf01 label rather than on the input's measured
    # noise, and is selected on width and ringing rather than on noise: the denoiser's gate would
    # pick whichever checkpoint irons the frame flattest, which is selecting a deconvolver for
    # blurring. Both switch together on purpose, because a run with one and not the other is a
    # combination nobody wants: labelled but scored wrong, or scored right but conditioned on the
    # wrong quantity.
    psf01_labels = None
    if args.cond_psf01:
        psf01_path = os.path.join(args.cache, "psf01.npy")
        if not os.path.exists(psf01_path):
            raise SystemExit(f"--cond-psf01 needs {psf01_path}; re-run --prepare against an export "
                             f"that carries degradations.jsonl with measured labels")

        psf01_labels = np.load(psf01_path)
        finite = np.isfinite(psf01_labels)
        if not finite.any():
            raise SystemExit("psf01.npy holds no finite label; every degraded tile lacked a "
                             "measurement, so there is nothing to condition on")

        cond_planes = 1
        print(f"conditioning on STORED psf01 ({int(finite.sum())} labelled slots, "
              f"p5 {np.quantile(psf01_labels[finite], 0.05):.3f} "
              f"p95 {np.quantile(psf01_labels[finite], 0.95):.3f}), not on measured noise")

    # E3.1: the unrolled Richardson-Lucy operator with a learned residual prior between iterations
    # (n2n_operator). The operator needs the deconvolution gate (--cond-psf01) and the supervised
    # regime against the clean master, and its labels are the nine-column rows load_operator_labels
    # packs (psf01, the row's kernel, the session's stretch) rather than psf01 alone.
    operator_labels = None
    if args.operator == "rl":
        import n2n_operator as OP
        if psf01_labels is None or not args.synthetic or args.synthetic_target != "master":
            raise SystemExit("--operator rl needs --cond-psf01 (the deconvolution gate) and --synthetic "
                             "against the master (the operator is supervised on the clean target)")
        operator_labels, _ = load_operator_labels(args.cache, meta)
        prior = OP.StretchedPrior(build_model(args.prior_base, args.upsample, 0), every=args.prior_every)
        model = OP.RLOperator(args.rl_k, prior=prior, recompute=args.recompute).to(dev)
        params = sum(p.numel() for p in model.parameters())
        print(f"E3.1 operator: Richardson-Lucy K={args.rl_k} with a residual U-Net prior (base {args.prior_base}, "
              f"every {args.prior_every} iteration(s), zero-initialised so step 0 IS E3.0), "
              f"{params/1e6:.2f} M params; the row's kernel and the session's stretch ride on the label planes")
    else:
        model = build_model(args.base, args.upsample, cond_planes).to(dev)
        params = sum(p.numel() for p in model.parameters())
    opt = torch.optim.Adam(model.parameters(), args.lr)
    print(f"device {dev}, U-Net base={args.base}, {params/1e6:.2f} M params, "
          f"{n_train} train cells, conditioning planes {cond_planes}")
    gate_labels = operator_labels if operator_labels is not None else psf01_labels

    # E2.8's star term. Only meaningful against the CLEAN target the stars were detected on, so it
    # is refused on any regime whose target is a noisy view (the stars.npy positions would then
    # index a tile nobody detected on).
    star_term = None
    star_w = None
    if args.star_loss is not None:
        if not args.synthetic or args.synthetic_target != "master":
            raise SystemExit("--star-loss needs --synthetic against the master: the star targets in "
                             "stars.npy were detected on slot 0")
        stars_path = os.path.join(args.cache, STARS_FILE)
        if not os.path.exists(stars_path):
            raise SystemExit(f"--star-loss needs {stars_path}; run --prepare-stars --cache {args.cache} "
                             f"(it adds the file to an existing cache without touching the tiles)")
        stars = np.load(stars_path)
        if stars.shape[0] != n:
            raise SystemExit(f"{stars_path} holds {stars.shape[0]} cells for a cache of {n}")
        empties = None
        if args.star_loss_empty:
            empty_path = os.path.join(args.cache, EMPTY_FILE)
            if not os.path.exists(empty_path):
                raise SystemExit(f"--star-loss-empty needs {empty_path}; re-run --prepare-stars")
            empties = np.load(empty_path)
            if empties.shape != stars.shape:
                raise SystemExit(f"{empty_path} is {empties.shape}, stars are {stars.shape}")
        star_term = StarTerm(stars, dev, empties)
        with_stars = int((stars[:n_train, :, 3] > 0).any(axis=1).sum())
        print(f"star-term loss ON: {int(stars[:n_train, :, 3].sum())} star targets over {with_stars}/{n_train} "
              f"train cells (<= {stars.shape[1]} a tile)"
              + (f" plus {int(empties[:n_train, :, 3].sum())} EMPTY windows weighted as stars" if empties is not None else "")
              + f"; weight {'matched to the pixel term on the first starred batch, then FIXED' if args.star_loss == 'auto' else args.star_loss}"
              + (f", re-fixed once at step {args.star_loss_refix}" if args.star_loss_refix else ""))

    # The mid-training probe. Loss cannot select a denoiser here (it falls fastest for a model
    # that irons the frame flat, because the background is most of the pixels) and neither can
    # PSNR. So selection runs on the two measures that reversed verdicts in the smoke runs.
    gate = None
    observers = []
    if args.gate_every > 0:
        import n2n_gate                       # imported here: it imports this module back
        cells = gate_cells(meta, args.gate_sessions, args.gate_cells)
        # The probe's noisy input: sub slot 1, or night A's half slot on a pair cache whose sub
        # slots are empty (n2n_gate.Gate's docstring).
        gate_input = SLOT_HALF_A if args.half_only else 1
        if psf01_labels is not None:
            import n2n_deconv_gate
            gate = n2n_deconv_gate.DeconvGate(
                mm, cells, dev, psf01=gate_labels[cells, gate_input - 1], input_slot=gate_input)
            print(f"deconv gate: {len(cells)} cells from {args.gate_sessions} val session(s), "
                  f"probing every {args.gate_every} steps; the probed input sits at "
                  f"{np.nanmean(gate.input_fwhm / gate.truth_fwhm):.2f}x the truth width")
        else:
            gate = n2n_gate.Gate(mm, cells, dev, input_slot=gate_input)
            print(f"gate: {len(cells)} cells from {args.gate_sessions} val session(s), probing every "
                  f"{args.gate_every} steps; floor {gate.floor_spurious:.1f} spurious/tile")
        if args.gate_observe:
            for s, ocells in observer_cells(meta, args.gate_sessions, args.gate_cells):
                # The observers have to be the SAME KIND of gate as the selector, or they report a
                # different set of keys under the selector's column headers. They were left as
                # denoiser gates when the selector became a deconvolution one, and the run died on
                # the first observed probe with a KeyError: better than printing noise-metric numbers
                # under width headings, which is what a looser formatter would have done.
                if psf01_labels is not None:
                    observers.append((s, n2n_deconv_gate.DeconvGate(
                        mm, ocells, dev, psf01=gate_labels[ocells, gate_input - 1],
                        input_slot=gate_input)))
                    # The observer's own star NULL is printed because E2.8b's kill line is stated
                    # against it ("under 2x the observer's input null"); E2.8's read had to compute it
                    # offline, and a number the log does not carry is a number a later reader guesses.
                    print(f"  OBSERVING (never selected on) {len(ocells)} cells from {s[:44]}; "
                          f"input at {np.nanmean(observers[-1][1].input_fwhm / observers[-1][1].truth_fwhm):.2f}x truth, "
                          f"input stars null {observers[-1][1].stars_null:.3f} (stars@{int(n2n_deconv_gate.STAR_SIGMA_LOW)} null "
                          f"{observers[-1][1].stars_null_lo:.3f})")
                else:
                    observers.append((s, n2n_gate.Gate(mm, ocells, dev, input_slot=gate_input)))
                    print(f"  OBSERVING (never selected on) {len(ocells)} cells from {s[:44]}; "
                          f"floor {observers[-1][1].floor_spurious:.1f} spurious/tile")
        # Names the three gates that are actually in the pass condition. It used to print the
        # residual threshold too, left behind when resid became report-only, which read as a
        # fourth gate in six runs' worth of logs.
        if psf01_labels is not None:
            # The star floor is ANCHORED ON THE MEASURED INPUT NULL, not on 1.0. The two bounds
            # answer to different references on purpose: destroying stars is measured against what
            # the model was HANDED (it cannot be blamed for the ones the blur already erased), while
            # inventing them is measured against the TRUTH (a deconvolution may not create a star the
            # clean master does not have). A single band centred on 1.0 conflated the two, sat above
            # the input's own 0.763, and failed 240 consecutive probes across six seeds.
            stars_floor = gate.stars_null * args.gate_min_stars_frac
            # The DECONVOLUTION criteria. Printed separately because the denoiser's lines below name
            # three thresholds none of which this run applies, and a log that states the wrong pass
            # condition is read as truth months later by whoever is diagnosing the run.
            print(f"  pass requires out/truth width >= 1.0 (under it is fabrication rather than "
                  f"success: an oracle handed the exact kernel never goes under) and stars kept in "
                  f"[{stars_floor:.3f}, {args.gate_max_stars_kept}]")
            print(f"  the star floor is {args.gate_min_stars_frac} x the INPUT's own measured "
                  f"{gate.stars_null:.3f}, because the blur is what erased those stars; the ceiling "
                  f"stays anchored on the truth, because fabrication is measured against it")
            print(f"  ring excess is REPORTED ONLY (nobody has calibrated what a value means yet)")
            print(f"  among passers the NARROWEST wins (doing nothing scores the input's own ratio)")
        else:
            print(f"  pass requires spurious-over-floor <= {args.gate_max_spurious}, faint amp >= "
                  f"{args.gate_min_faint_amp}, noise <= {args.gate_max_noise}x")
            print(f"  |resid corr| is REPORTED ONLY (it does not transfer between sessions)")
            print(f"  among passers the QUIETEST wins (doing nothing is 1.00x, the worst answer)")
        header = (n2n_deconv_gate.DeconvGate.header() if psf01_labels is not None
                  else n2n_gate.Gate.header())
        print(f"  step   {header}   {'obj':>6}  {'':4}")

    band_scales = [tuple(float(v) for v in p.split(",")) for p in args.band_scales.split()]
    kernels = {s: _gauss_kernel(s, dev) for pair in band_scales for s in pair}
    # --pair-time restricts which sub pairs may train, by SLOT distance. Slots 1..8 are the
    # exported subs in chronological order (sampled spread across the session), so slot distance
    # is a monotone proxy for time separation. v22 measured that residuals of time-adjacent subs
    # correlate 1.5-3x more than distant ones on every session (time-correlated residue: seeing
    # bursts, drift, walking pattern -- the N2N independence premise violated), and that the
    # many-session models transfer a residue-keeping disposition to unseen sessions. "far" trains
    # only on the pairs whose shared residue is smallest; "near" only on the most contaminated
    # pairs, as the dose control. "any" leaves the draw byte-for-byte on the original stream.
    pair_pool = None
    if args.pair_time == "far":
        pair_pool = np.array([(p, q) for p in range(1, SUBS_PER_CELL + 1)
                              for q in range(1, SUBS_PER_CELL + 1) if abs(p - q) >= 4],
                             dtype=np.int64)
    elif args.pair_time == "near":
        pair_pool = np.array([(p, q) for p in range(1, SUBS_PER_CELL + 1)
                              for q in range(1, SUBS_PER_CELL + 1) if abs(p - q) == 1],
                             dtype=np.int64)
    if pair_pool is not None:
        print(f"pair-time {args.pair_time}: {len(pair_pool)} of "
              f"{SUBS_PER_CELL * (SUBS_PER_CELL - 1)} ordered sub pairs; averaged regimes use "
              f"{'blocked' if args.pair_time == 'far' else 'interleaved'} splits")
    rng = np.random.default_rng(args.seed)
    steps = args.steps
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=steps)
    t0 = time.perf_counter()
    running = []
    running_star = []
    regime_steps = defaultdict(int)
    best = (-1.0, 0, None, None)      # (score, step, metrics, state_dict)
    # The same tuple, tracked WITHOUT the noise threshold: the best-by-noise probe among those
    # meeting the structure criteria. It never becomes --out, and exists for the case below where
    # no probe passes all three, because the fallback there is the FINAL weights and those can be
    # strictly worse than a probe at equal noise. Measured on the WIDE arm 2026-09-06: seed 1's
    # step 2200 and its step 4000 both read 0.880x, but 2200 holds faint amplitude 0.82 against
    # 0.76 and sits 14.6 under the spurious floor against 23.8. Saving it alongside makes that
    # visible instead of leaving it to a re-run with a different threshold.
    best_struct = (-1.0, 0, None, None)
    for step in range(1, steps + 1):
        idx = rng.integers(0, n_train, args.batch)
        # Two DIFFERENT subs of the same cell: independent noise, same scene. That is the
        # whole N2N premise, and it is why no clean target is needed.
        a = rng.integers(1, SUBS_PER_CELL + 1, args.batch)
        b = (a - 1 + rng.integers(1, SUBS_PER_CELL, args.batch)) % SUBS_PER_CELL + 1
        if pair_pool is not None:
            # Overrides the draw above instead of replacing it, so --pair-time any consumes the
            # rng stream exactly as every earlier version did and stays comparable against them.
            pick = pair_pool[rng.integers(0, len(pair_pool), args.batch)]
            a, b = pick[:, 0], pick[:, 1]
        # Which regime this step trains at. Drawn per step, so ONE model sees the 1-sub, 2-sub,
        # 4-sub and (with --half-pairs) half-master noise levels; paired with --cond that turns
        # denoising strength into a function the model learns rather than a constant it assumes.
        k = regimes[int(rng.integers(0, len(regimes)))] if len(regimes) > 1 else regimes[0]
        regime_steps[k] += 1
        if k == SYNTH:
            # Input: one injected draw. Target: the undegraded tile every draw was made from, or on a
            # pair cache whichever night --synthetic-target names.
            x = torch.from_numpy(np.ascontiguousarray(mm[idx, a])).to(dev).float()
            y = torch.from_numpy(np.ascontiguousarray(mm[idx, synth_target])).to(dev).float()
        elif k == HALF:
            # The two halves are already integrated, so there is nothing to average: this is a
            # plain N2N pair that happens to be quiet. The split is INTERLEAVED upstream
            # (SessionRegistrar takes i%2), so the two sides are statistically exchangeable and
            # neither is "early in the night"; the swap is only so the model cannot learn a
            # systematic a->b direction from the slot order.
            hidx = half_train[rng.integers(0, half_train.size, args.batch)]
            swap = rng.random(args.batch) < 0.5
            sa = np.where(swap, SLOT_HALF_B, SLOT_HALF_A)
            sb = np.where(swap, SLOT_HALF_A, SLOT_HALF_B)
            x = torch.from_numpy(np.ascontiguousarray(mm[hidx, sa])).to(dev).float()
            y = torch.from_numpy(np.ascontiguousarray(mm[hidx, sb])).to(dev).float()
        elif k > 1:
            # Disjoint halves of the cell's 8 subs, averaged. Both sides stay independent, so
            # N2N still holds, but the noise level now resembles what the model meets at
            # inference on an integrated master rather than a single frame.
            if pair_pool is None:
                perm = np.stack([rng.permutation(SUBS_PER_CELL) + 1 for _ in range(args.batch)])
                side_x = perm[:, :k]
                side_y = perm[:, k:2 * k]
            else:
                # far: the sides come from opposite time-blocks (slots 1-4 vs 5-8), so the
                # averaged pair is as time-separated as 8 slots allow; near: the sides interleave
                # odd and even slots, the maximally time-mixed split (the half-master
                # construction), so they share the most drift. The random swap keeps the a->b
                # direction unlearnable, same reason as the half-pair regime's.
                if args.pair_time == "far":
                    g1 = np.arange(1, SUBS_PER_CELL // 2 + 1)
                    g2 = np.arange(SUBS_PER_CELL // 2 + 1, SUBS_PER_CELL + 1)
                else:
                    g1 = np.arange(1, SUBS_PER_CELL + 1, 2)
                    g2 = np.arange(2, SUBS_PER_CELL + 1, 2)
                s1 = np.stack([rng.permutation(g1)[:k] for _ in range(args.batch)])
                s2 = np.stack([rng.permutation(g2)[:k] for _ in range(args.batch)])
                swap = rng.random(args.batch) < 0.5
                side_x = np.where(swap[:, None], s2, s1)
                side_y = np.where(swap[:, None], s1, s2)
            xs = np.stack([mm[idx[j], side_x[j]].mean(axis=0) for j in range(args.batch)])
            ys = np.stack([mm[idx[j], side_y[j]].mean(axis=0) for j in range(args.batch)])
            x = torch.from_numpy(xs).to(dev).float()
            y = torch.from_numpy(ys).to(dev).float()
        else:
            x = torch.from_numpy(np.ascontiguousarray(mm[idx, a])).to(dev).float()
            y = torch.from_numpy(np.ascontiguousarray(mm[idx, b])).to(dev).float()

        if psf01_labels is not None:
            # Each sample's label follows the SLOT it was drawn from, since that is the tile the
            # exporter measured. A cell with an unlabelled slot falls back to the batch's median
            # rather than to zero, which would tell the model "no blur" about a blurred tile.
            import n2n_deconv_gate as DG
            if operator_labels is not None:
                # The operator's nine label columns per drawn slot; a NaN (an unlabelled psf01, the
                # one column that can be) takes the batch's column median, never zero.
                lab = operator_labels[idx, np.clip(a - 1, 0, operator_labels.shape[1] - 1)].copy()
                for col in range(lab.shape[1]):
                    bad = ~np.isfinite(lab[:, col])
                    if bad.any():
                        good = lab[~bad, col]
                        lab[bad, col] = float(np.median(good)) if good.size else 0.5
            else:
                lab = psf01_labels[idx, np.clip(a - 1, 0, psf01_labels.shape[1] - 1)]
                if not np.all(np.isfinite(lab)):
                    good = lab[np.isfinite(lab)]
                    lab = np.where(np.isfinite(lab), lab, float(np.median(good)) if good.size else 0.5)
            pred = model(DG.with_psf01(x, lab))
        else:
            pred = model(with_sigma(x, planes=cond_planes) if cond_planes else x)
        # Mask the rim: at inference no output pixel comes from a chunk edge, so a loss over
        # the full tile optimises a condition the model never meets.
        pc = pred[:, :, BORDER:-BORDER, BORDER:-BORDER]
        yc = y[:, :, BORDER:-BORDER, BORDER:-BORDER]
        # L1 converges to the conditional MEDIAN, which for a star near the noise floor sits
        # at the background: an L1 N2N erases faint stars while scoring well on PSNR, because
        # PSNR is dominated by the background pixels it cleans beautifully. L2 converges to the
        # conditional MEAN, which is unbiased and preserves faint flux in expectation.
        pixel = (nn.functional.l1_loss(pc, yc) if args.loss == "l1"
                 else nn.functional.mse_loss(pc, yc))
        loss = pixel

        # Structure-preserving term. Plain L2 is dominated by the flat background, which is
        # most of the frame, so the cheapest way for the model to lower it is to iron out fine
        # detail. Matching the DIFFERENCE-OF-GAUSSIANS bands as well puts explicit weight on
        # the scales that were measured to be damaged (1-2 px worst).
        #
        # This stays unbiased under N2N: the target is another noisy frame, but its bandpass
        # is the clean bandpass plus zero-mean noise, and a SQUARED penalty converges to the
        # conditional mean either way. It would NOT be safe with an L1 band term, which would
        # chase the target's own noise realisation.
        #
        # Which bands to supervise is NOT a free choice: measured on this data the 1-2 px band
        # of a single sub carries 5.18x the master's RMS, i.e. it is almost pure noise, so its
        # gradient is dominated by the target's own noise realisation. Unbiased but very high
        # variance, which at 4000 steps behaves like gradient noise. Hence --band-scales.
        if args.band_loss > 0:
            band = 0.0
            for s1, s2 in band_scales:
                k1, k2 = kernels[s1], kernels[s2]
                band = band + nn.functional.mse_loss(
                    _blur(pc, k1) - _blur(pc, k2), _blur(yc, k1) - _blur(yc, k2))
            loss = loss + args.band_loss * band / len(band_scales)

        # The star term (E2.8). Its weight is set on the FIRST batch that carries a star so the term
        # equals the pixel term there, then never moves: logged once, saved in the checkpoint, and
        # never tuned on the gate.
        if star_term is not None:
            s_term, n_stars = star_term(pc, yc, idx)
            if n_stars > 0:
                if star_w is None:
                    if args.star_loss == "auto":
                        star_w = float(pixel.item()) / max(float(s_term.item()), 1e-12)
                        print(f"  star-loss weight FIXED at {star_w:.4e} on step {step}: pixel term "
                              f"{pixel.item():.4e} / star term {s_term.item():.4e} over {n_stars} stars",
                              flush=True)
                    else:
                        star_w = float(args.star_loss)
                        print(f"  star-loss weight {star_w:.4e} (given)", flush=True)
                elif args.star_loss_refix and step == args.star_loss_refix:
                    # E2.8b arm W: the first batch's pixel term is the injected noise, not the task,
                    # so the weight matched there left the term tens of times the pixel term once the
                    # noise was gone. Re-matched ONCE here, logged, and fixed again.
                    refixed = float(pixel.item()) / max(float(s_term.item()), 1e-12)
                    print(f"  star-loss weight RE-FIXED at {refixed:.4e} on step {step} (was {star_w:.4e}): pixel term "
                          f"{pixel.item():.4e} / star term {s_term.item():.4e} over {n_stars} stars", flush=True)
                    star_w = refixed
                loss = loss + star_w * s_term
                running_star.append(float(s_term.item()))
        opt.zero_grad(set_to_none=True)
        loss.backward()
        opt.step()
        sched.step()
        running.append(loss.item())

        if step % args.log_every == 0 or step == steps:
            el = time.perf_counter() - t0
            star_note = (f"  star {np.mean(running_star[-args.log_every:]):.4f}"
                         if star_term is not None and running_star else "")
            print(f"  step {step:6d}/{steps}  loss {np.mean(running[-args.log_every:]):.5f}{star_note}  "
                  f"{step*args.batch/el:5.1f} tiles/s  elapsed {el/60:5.1f} min", flush=True)

        if gate is not None and (step % args.gate_every == 0 or step == steps):
            m = gate.evaluate(model) if psf01_labels is not None else gate.evaluate(model, cond_planes)
            # Three hard gates, then MINIMISE noise among whatever passes. Framing invention,
            # residual correlation and faint-flux retention as GATES rather than as terms in a
            # weighted score is deliberate: a weight lets a model buy its way past invention with
            # noise reduction, which is the trade every failed variant made.
            #
            # Minimising noise, rather than maximising a faint_amp/noise ratio, is also
            # deliberate and was a bug first: that ratio is maximised by DOING NOTHING (an
            # identity model scores exactly 1.0 and an almost-identity one slightly above it, so
            # a 600-step net beat the finished v9h on it) and it passes every other gate
            # trivially, because a model that changes nothing invents nothing. Noise as the
            # objective makes the identity the WORST possible answer at 1.0x, and the gates then
            # bound what the cleaning is allowed to cost. Which is also the trade already made by
            # hand: v8 was picked at 0.62 faint amplitude over a variant holding 0.75, because it
            # cleaned harder.
            # A FLOOR on the denoising, because minimising noise among passers still ships an
            # identity when the identity is the only passer. That happened: one seed's selection
            # read 0.849x on the gate session and 1.02x on the report's, so it had been chosen as
            # a strong denoiser and was doing nothing. A run with no probe that both cleans and
            # stays pure should SAY so, not hand back the cheapest way to satisfy a purity gate.
            #
            # Residual correlation is reported and NOT gated, which is a reversal: it was the
            # binding gate, then a relaxed one, and measuring it across two held-out sessions
            # showed it does not transfer at all. Session-to-session delta reaches 0.301 for one
            # checkpoint (+0.223 against -0.078, same weights) while the spread ACROSS six very
            # different checkpoints on one session is only 0.160. A metric whose session shift is
            # twice its model signal cannot threshold or rank, and no choice of threshold repairs
            # that. faint_amp transfers ~6:1 (delta <=0.047, spread 0.271), so it carries the gate.
            #
            # The fabrication count does NOT transfer either, and the line above used to claim it
            # did. Measured over seven checkpoints on two held-out sessions, spurious_over_floor
            # shifts +4.3 to +8.1 between them against a 4.9 spread across models on one session.
            # So the constant below is SESSION-CALIBRATED and not a universal purity bar: the same
            # weights that sit 3.3 over the floor on one session sit 1.0 UNDER it on another, and
            # 6.0 admits or rejects accordingly. Subtracting the raw-sub floor was supposed to
            # normalise exactly this and does not, because the shift is systematic and one-signed
            # rather than a per-session offset the floor tracks. So read this gate as ordering steps
            # within one run on one session, which is its actual job, and never as a portable claim
            # about a checkpoint's purity.
            #
            # No reformulation repairs it either: six candidates scored offline (n2n_gatenorm.py),
            # none reaching a usable threshold, and this difference-of-means is the best of them.
            # But the per-tile log ratio preserves the ORDERING across sessions (rho +0.86 against
            # +0.54 here), which is all a stopping rule needs, so `log_ratio` is reported on every
            # probe to make a relative rule testable. Do not gate on it yet: whether a relative rule
            # picks the same step on two sessions is the open question, which --gate-observe exists
            # to answer.
            if psf01_labels is not None:
                # A deconvolver's criteria, and deliberately only ONE of them is a threshold.
                # `fwhm_ratio >= 1` is not a tuning knob: E1 measured that an oracle handed the
                # EXACT kernel never produces a star narrower than the one that was there, so
                # crossing it is fabrication rather than success. `stars_kept` guards the other
                # failure the same measurements kept catching, a width that improves because noise
                # was sharpened into a new population. Ring excess is REPORTED and not thresholded,
                # for the reason `resid_corr` is report-only above: nobody has calibrated what value
                # means anything, and a threshold nobody measured is how the noise gate came to
                # reject the arm that scored best.
                # Bounded on BOTH sides, and the upper bound is the one that matters. The failure is
                # symmetric: a deconvolver can destroy the star population or invent one, and it is
                # the second that flatters every other number, because sharpened noise reads as
                # narrow stars. Seen immediately on the first smoke run, where a 200-step model
                # produced TEN TIMES the truth's detections and sailed through a lower bound alone.
                # A deconvolution cannot legitimately create a star the clean master does not have.
                structure_ok = (stars_floor <= m["stars_kept"] <= args.gate_max_stars_kept)
                passed = structure_ok and np.isfinite(m["fwhm_ratio"]) and m["fwhm_ratio"] >= 1.0
                # Closest to the truth width from ABOVE. Doing nothing scores the input's own
                # ratio, which is the worst answer rather than a free pass.
                score = m["fwhm_ratio"] if np.isfinite(m["fwhm_ratio"]) else float("inf")
            else:
                structure_ok = (m["spurious_over_floor"] <= args.gate_max_spurious
                                and m["faint_amp"] >= args.gate_min_faint_amp)
                passed = structure_ok and m["noise"] <= args.gate_max_noise
                score = m["noise"]
            mark = "pass" if passed else "FAIL"
            if passed and (best[3] is None or score < best[0]):
                best = (score, step, m,
                        {k: v.detach().cpu().clone() for k, v in model.state_dict().items()})
                mark = "pass *"
            if structure_ok and (best_struct[3] is None or score < best_struct[0]):
                best_struct = (score, step, m,
                               {k: v.detach().cpu().clone() for k, v in model.state_dict().items()})
            fmt = (n2n_deconv_gate.DeconvGate.format if psf01_labels is not None
                   else n2n_gate.Gate.format)
            print(f"  gate {step:6d}   {fmt(m)}   {score:6.3f}  {mark}",
                  flush=True)
            # The observed sessions print on the SAME schedule with an "obs" tag and no verdict
            # column, so the two trajectories are aligned step-for-step in one log and neither can
            # be mistaken for the one that selected.
            for si, (_, og) in enumerate(observers):
                om = og.evaluate(model) if psf01_labels is not None else og.evaluate(model, cond_planes)
                print(f"  obs{si} {step:6d}   {fmt(om)}", flush=True)

    if len(regimes) > 1:
        print("  steps per regime: " + "  ".join(
            f"{k}={regime_steps[k]}" for k in regimes))

    def save(state, path, selected_at):
        torch.save({"model": state, "base": args.base, "upsample": args.upsample,
                    "cond": cond_planes, "half_pairs": args.half_pairs,
                    "regimes": [str(k) for k in regimes], "selected_at_step": selected_at,
                    "pair_time": args.pair_time, "star_loss_w": star_w,
                    "operator": args.operator, "rl_k": args.rl_k,
                    "prior_base": args.prior_base, "prior_every": args.prior_every},
                   os.path.join(args.cache, path))
        print(f"saved -> {os.path.join(args.cache, path)}")

    if gate is not None and best[3] is not None:
        # The FINAL weights are kept beside the selected ones rather than discarded, so the
        # choice stays auditable: if the last step also passes, "selection helped" has to be
        # demonstrated against it, not assumed.
        final_out = args.out_final or args.out.replace(".pt", "_final.pt")
        save(best[3], args.out, best[1])
        print(f"  selected step {best[1]} of {steps}, score {best[0]:.3f}")
        save(model.state_dict(), final_out, steps)
    else:
        if gate is not None:
            print("  NO probe passed every gate; saving the final weights and saying so rather "
                  "than quietly shipping the least-bad one.")
            # Which criterion blocked it is the useful half, and the two answers mean opposite
            # things. If the STRUCTURE criteria were met throughout and only the noise threshold
            # failed, the run is a gentle model on this val, not a bad one, and the threshold is
            # an absolute on a quantity that trades against the criteria it is paired with -- read
            # the arm's own eval, not this. If the structure criteria failed, the run really did
            # fabricate or flatten and the fallback weights deserve the suspicion.
            #
            # This gate orders STEPS WITHIN ONE RUN ON ONE SESSION (see the note beside the
            # criteria above); it is not a portable purity bar, so "N of 3 seeds failed the gate"
            # is not a fact about an arm. Measured 2026-09-06: all 120 WIDE probes met the
            # structure criteria and only 20 met the noise one, while the arm it was being
            # compared against violated the structure criteria on most of its probes and reached a
            # lower noise where it did not. Same gate, opposite failure, and the arm that never
            # fabricates is the one it rejects.
            if best_struct[3] is not None:
                struct_out = args.out.replace(".pt", "_bestprobe.pt")
                print(f"  the structure criteria WERE met at {best_struct[1]} (noise "
                      f"{best_struct[0]:.3f}x, over the {args.gate_max_noise} threshold); saving "
                      f"that probe beside the final weights for audit, NOT as the output.")
                save(best_struct[3], struct_out, best_struct[1])
            else:
                print("  no probe met the structure criteria either, so the run has nothing to "
                      "audit against: it fabricated or flattened at every probe.")
        save(model.state_dict(), args.out, steps)


# --------------------------------------------------------------------------- the operator (E3)
def load_operator_labels(cache, meta):
    """`[cells, SUBS_PER_CELL, n2n_operator.LABEL_COUNT]`: psf01, the kernel (fwhm, beta), the
    session stretch (3 mins, 3 betas), in the plane order `n2n_operator` unpacks."""
    import n2n_operator as OP
    n = meta["cells"]
    psf01_path = os.path.join(cache, "psf01.npy")
    kernels_path = os.path.join(cache, OP.KERNELS_FILE)
    stretch_path = os.path.join(cache, OP.STRETCH_FILE)
    for p in (kernels_path, stretch_path):
        if not os.path.exists(p):
            raise SystemExit(f"the operator needs {p}; re-run --prepare with --bake against an export "
                             f"that carries the estimated-kernel columns")
    psf01 = np.load(psf01_path) if os.path.exists(psf01_path) else np.full((n, SUBS_PER_CELL), np.nan, np.float32)
    kernels = np.load(kernels_path)
    stretch = np.load(stretch_path)
    labels = np.full((n, SUBS_PER_CELL, OP.LABEL_COUNT), np.nan, dtype=np.float32)
    labels[..., OP.LABEL_PSF01] = psf01
    labels[..., OP.LABEL_KERNEL_FWHM] = kernels[..., 0]
    labels[..., OP.LABEL_KERNEL_BETA] = kernels[..., 1]
    labels[..., OP.LABEL_MIN] = stretch[:, None, :3]
    labels[..., OP.LABEL_BETA] = stretch[:, None, 3:]
    return labels, kernels


def operator_only(args):
    """E3.0: the operator ALONE through the gate. No training, no parameters, one probe.

    Pre-registered (docs/plans/deconvolver-training.md, "What is pre-registered before a seed is
    trained"): K = 20 with the estimated per-tile kernel, no network; selects at out/truth at or under
    1.10 with stars at or over 0.60 and the observer under 2x its null; a KILL at out/truth over 1.20
    or the observer over 2x, which says the tile-wise kernel or the unrolling is wrong before any
    learning has been asked for. The verdict lines below state those numbers so the log carries the
    pass condition next to the reading (the trainer's own rule).
    """
    import torch
    import n2n_deconv_gate as DG
    import n2n_operator as OP
    mm, meta = open_cache(args.cache)
    labels, kernels = load_operator_labels(args.cache, meta)
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = OP.RLOperator(args.rl_k).to(dev)
    gate_input = 1
    cells = gate_cells(meta, args.gate_sessions, args.gate_cells)
    lab = labels[cells, gate_input - 1]
    if not np.all(np.isfinite(lab[:, 1:])):
        bad = int((~np.isfinite(lab[:, 1:]).all(axis=1)).sum())
        raise SystemExit(f"{bad} of {len(cells)} gate cells lack a kernel or stretch label; the operator "
                         f"cannot run on a cell it has no kernel for")
    estimated = kernels[cells, gate_input - 1, 3]
    print(f"E3.0 operator: Richardson-Lucy K={args.rl_k}, the row's kernel "
          f"({int(estimated.sum())}/{len(cells)} gate cells on an ESTIMATED kernel, the rest on the drawn "
          f"kernel's effective width), device {dev}, {sum(p.numel() for p in model.parameters())} parameters")
    print(f"  kernel fwhm on the gate cells p5 {np.quantile(lab[:, 1], 0.05):.2f} p50 {np.quantile(lab[:, 1], 0.5):.2f} "
          f"p95 {np.quantile(lab[:, 1], 0.95):.2f} px, beta p50 {np.quantile(lab[:, 2], 0.5):.2f}")
    gate = DG.DeconvGate(mm, cells, dev, psf01=lab, input_slot=gate_input)
    print(f"deconv gate: {len(cells)} cells from {args.gate_sessions} val session(s); the probed input sits at "
          f"{np.nanmean(gate.input_fwhm / gate.truth_fwhm):.2f}x the truth width; input stars null "
          f"{gate.stars_null:.3f} (stars@{int(DG.STAR_SIGMA_LOW)} null {gate.stars_null_lo:.3f})")
    observers = []
    if args.gate_observe:
        for s, ocells in observer_cells(meta, args.gate_sessions, args.gate_cells):
            olab = labels[ocells, gate_input - 1]
            og = DG.DeconvGate(mm, ocells, dev, psf01=olab, input_slot=gate_input)
            observers.append((s, og))
            print(f"  OBSERVING {len(ocells)} cells from {s[:44]}; input at "
                  f"{np.nanmean(og.input_fwhm / og.truth_fwhm):.2f}x truth, input stars null {og.stars_null:.3f} "
                  f"(stars@{int(DG.STAR_SIGMA_LOW)} null {og.stars_null_lo:.3f})")
    print(f"  pass: out/truth <= 1.10 with stars >= 0.60 and every observer's stars under 2x its null; "
          f"KILL: out/truth > 1.20 or an observer over 2x its null")
    print(f"           {DG.DeconvGate.header()}")
    t0 = time.perf_counter()
    m = gate.evaluate(model)
    elapsed = time.perf_counter() - t0
    print(f"  gate     {DG.DeconvGate.format(m)}   ({elapsed:.1f} s)")
    obs = []
    for si, (s, og) in enumerate(observers):
        om = og.evaluate(model)
        obs.append((s, om, og.stars_null))
        print(f"  obs{si}     {DG.DeconvGate.format(om)}   null {og.stars_null:.3f} -> {om['stars_kept'] / og.stars_null:.2f}x")
    width_ok = np.isfinite(m["fwhm_ratio"]) and m["fwhm_ratio"] <= 1.10
    stars_ok = m["stars_kept"] >= 0.60
    obs_ok = all(np.isfinite(om["stars_kept"]) and om["stars_kept"] < 2.0 * null for _, om, null in obs)
    killed = (np.isfinite(m["fwhm_ratio"]) and m["fwhm_ratio"] > 1.20) or not obs_ok
    verdict = "KILLED" if killed else ("PASS" if width_ok and stars_ok else "NEITHER (between the pass and the kill line)")
    print(f"  E3.0 verdict at K={args.rl_k}: {verdict}  (out/truth {m['fwhm_ratio']:.3f}, stars {m['stars_kept']:.2f}, "
          f"observers {'ok' if obs_ok else 'OVER 2x null'})")
    out = os.path.join(args.cache, f"e30_operator_k{args.rl_k}.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump({"k": args.rl_k, "gate_cells": len(cells), "estimated_kernels": int(estimated.sum()),
                   "gate": m, "input_ratio": float(np.nanmean(gate.input_fwhm / gate.truth_fwhm)),
                   "stars_null": gate.stars_null,
                   "observers": [{"session": s, "metrics": om, "stars_null": null} for s, om, null in obs],
                   "verdict": verdict, "seconds": elapsed}, fh, indent=1)
    print(f"  written -> {out}")


# --------------------------------------------------------------------------- eval
def crop(t):
    return t[..., BORDER:-BORDER, BORDER:-BORDER]


def psnr(a, b):
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return 10 * np.log10(1.0 / mse) if mse > 0 else float("inf")


def evaluate(args):
    import torch
    from scipy.ndimage import gaussian_filter

    mm, meta = open_cache(args.cache)
    n, n_train = meta["cells"], meta["train_cells"]

    ck = torch.load(os.path.join(args.cache, "n2n.pt"), map_location="cpu")
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = build_model(ck["base"]).to(dev)
    model.load_state_dict(ck["model"])
    model.eval()

    val = range(n_train, n)
    raw_p, den_p, gau_p = [], [], []
    best_sigma = args.sigma
    with torch.no_grad():
        for i in val:
            master = np.asarray(mm[i, 0], dtype=np.float32)
            sub = np.asarray(mm[i, 1], dtype=np.float32)
            den = model(torch.from_numpy(sub)[None].to(dev)).cpu().numpy()[0]
            gau = np.stack([gaussian_filter(sub[c], best_sigma) for c in range(CH)])

            m = crop(master)
            raw_p.append(psnr(crop(sub), m))
            den_p.append(psnr(crop(den), m))
            gau_p.append(psnr(crop(gau), m))

    print(f"\nHeld-out sessions: {', '.join(meta['val_sessions'])}")
    print(f"Evaluated {len(raw_p)} cells, measured against the session master "
          f"over the central {TILE-2*BORDER}px\n")
    print(f"  raw sub          PSNR {np.mean(raw_p):6.2f} dB")
    print(f"  gaussian s={best_sigma:<4}   PSNR {np.mean(gau_p):6.2f} dB  "
          f"({np.mean(gau_p)-np.mean(raw_p):+.2f} dB)")
    print(f"  N2N denoised     PSNR {np.mean(den_p):6.2f} dB  "
          f"({np.mean(den_p)-np.mean(raw_p):+.2f} dB)")
    print(f"\n  N2N vs gaussian: {np.mean(den_p)-np.mean(gau_p):+.2f} dB")
    won = sum(1 for d, r in zip(den_p, raw_p) if d > r)
    print(f"  N2N beat the raw sub on {won}/{len(raw_p)} cells")

    # Visual: raw | denoised | master, stretched identically per row.
    if args.png:
        from PIL import Image as PImage
        rows = []
        for i in list(val)[:args.png_cells]:
            master = np.asarray(mm[i, 0], dtype=np.float32)
            sub = np.asarray(mm[i, 1], dtype=np.float32)
            with torch.no_grad():
                den = model(torch.from_numpy(sub)[None].to(dev)).cpu().numpy()[0]
            trio = [crop(sub), crop(den), crop(master)]
            trio = [np.clip(t.transpose(1, 2, 0), 0, 1) for t in trio]
            rows.append(np.concatenate(trio, axis=1))
        img = (np.concatenate(rows, axis=0) * 255).astype(np.uint8)
        out = os.path.join(args.cache, "compare_raw_denoised_master.png")
        PImage.fromarray(img).save(out)
        print(f"\n  wrote {out}  (columns: raw sub | N2N denoised | master)")


# --------------------------------------------------------------------------- cli
if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--root", default=None,
                   help="the bake the tiles come from (its tiles-manifest.jsonl is --manifest). "
                        "Required with --prepare and unused otherwise. Deliberately no default: the "
                        "shipped arm was prepared from 2025-2026-darkscaled while the original smoke "
                        "runs read 2025-2026-calgated, and a default pointing at either is how a "
                        "cache silently gets its tiles from the wrong bake")
    p.add_argument("--manifest", default="tiles-manifest.jsonl")
    p.add_argument("--cache", required=True,
                   help="the prepared cache: written by --prepare, read by --train and --eval, and "
                        "where --out lands (the checkpoint is saved INSIDE this directory, so a "
                        "re-run of a recipe needs its own --out name or it overwrites the reference). "
                        "No default, see n2n_paths.py")
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--train", action="store_true")
    p.add_argument("--eval", action="store_true")
    p.add_argument("--train-sessions", type=int, default=8)
    p.add_argument("--val-sessions", type=int, default=2)
    p.add_argument("--cells-per-session", type=int, default=120)
    p.add_argument("--val-from-list", default=None,
                   help="pin the val sessions BY NAME to a list file, one name per line. The "
                        "companion to --train-from-list, for when sessions must be EXCLUDED from "
                        "both splits (E2 excludes the eval4 observers' own scenes). Wins over "
                        "--val-from-meta when both are given")
    p.add_argument("--val-from-meta", default=None,
                   help="pin the val sessions BY NAME to those recorded in an existing cache's "
                        "meta.json. Without it, raising --train-sessions moves the val split and "
                        "pulls the previously held-out sessions into training, silently rebasing "
                        "every session-calibrated gate threshold. By name, not by index: the "
                        "shuffle runs over the whole session list, so baking one more session "
                        "reorders all of it")
    p.add_argument("--train-from-list", default=None,
                   help="file of train session names, one per line. Overrides --train-sessions. "
                        "For subset arms: pinning by name keeps the arm a true SUBSET of the run "
                        "it is compared against, which dropping-and-reslicing does not.")
    p.add_argument("--val-cells-per-session", type=int, default=None,
                   help="cells per VAL session (default: --cells-per-session). Lets the train "
                        "set trade cells-per-session for session COUNT while val keeps enough "
                        "cells for --gate-cells to draw the same sample as earlier runs")
    p.add_argument("--base", type=int, default=32)
    p.add_argument("--batch", type=int, default=8)
    p.add_argument("--lr", type=float, default=2e-4)
    p.add_argument("--steps", type=int, default=4000)
    p.add_argument("--nondeterministic", action="store_true",
                   help="let cudnn autotune. Faster, but two runs of one seed then differ, so "
                        "only use it when no comparison depends on the result")
    p.add_argument("--seed", type=int, default=0,
                   help="seeds BOTH the weight init and the tile draw. An A/B between two configs "
                        "needs several seeds per config: the run-to-run spread from init alone is "
                        "as large as the between-config difference on the selection metrics")
    p.add_argument("--log-every", type=int, default=200)
    p.add_argument("--loss", choices=("l1", "l2"), default="l1")
    p.add_argument("--upsample", action="store_true")
    p.add_argument("--pair-avg", type=int, default=1)
    p.add_argument("--mix-avg", action="store_true")
    p.add_argument("--pair-time", choices=("any", "far", "near"), default="any",
                   help="restrict training pairs by slot (time) separation: 'far' pairs only "
                        "subs >=4 slots apart (and blocks the averaged regimes 1-4 vs 5-8), "
                        "'near' only adjacent subs (and interleaves the averages), 'any' is the "
                        "original unrestricted draw, byte-for-byte. v22 measured time-correlated "
                        "residue in every session's pairs; this is the causal test")
    p.add_argument("--synthetic-target", choices=["master", "half-a", "half-b"], default="master",
                   help="which frame --synthetic regresses onto. On a cross-night PAIR cache the half "
                        "slots are two nights of one sky: half-a shares the input's noise (the input is "
                        "that night degraded), half-b does not. Holding the input distribution fixed and "
                        "moving only this is the one-axis test of whether a shared-noise target is what "
                        "limits a denoiser (H8)")
    p.add_argument("--synthetic", action="store_true",
                   help="supervised regime: train an injected draw against the CLEAN target in "
                        "slot 0, instead of one noisy view against another. Needs a cache prepared "
                        "from `tianwen dataset degrade`, and is exclusive of the N2N regimes so the "
                        "arm answers H1. The target is a master, so it carries its own 1/sqrt(N) "
                        "noise: score against a held-out half, never against the target itself")
    p.add_argument("--half-pairs", action="store_true",
                   help="train on the half-master pair as a fourth regime (needs a cache "
                        "prepared from a bake that exports halves)")
    p.add_argument("--half-only", action="store_true",
                   help="train on the half-master pair ALONE: the regime for a cross-night pair cache "
                        "(tianwen dataset pair), whose two nights sit in the half slots and whose sub "
                        "slots are empty. Implies --half-pairs")
    p.add_argument("--require-halves", action="store_true",
                   help="prepare only from sessions that carry a half-master pair")
    p.add_argument("--cond", action="store_true",
                   help="condition on ONE plane holding the tile's own background sigma")
    p.add_argument("--cond-bands", action="store_true",
                   help="condition on a per-band noise PROFILE instead of one scalar (implies "
                        "--cond and overrides it). A scalar cannot express noise SHAPE, and "
                        "measured scene-free the shape is what separates the training regimes "
                        "from the deployment one: all three sub-derived regimes share "
                        "band1/band0 = 0.601/0.596/0.589 while a half-master reads 0.320. So "
                        "sub-averaging closed the LEVEL half of the deployment gap only, and one "
                        "number was labelling two different distributions")
    p.add_argument("--band-loss", type=float, default=0.0)
    p.add_argument("--band-scales", default="1,2 2,4 4,8")
    p.add_argument("--star-loss", default=None,
                   help="E2.8: add the star-term loss (StarTerm) over the stars.npy targets. 'auto' "
                        "sets its weight ONCE so the term equals the pixel term on the first starred "
                        "batch and then fixes it (logged, saved in the checkpoint); a number is used as "
                        "given. Needs --synthetic against the master and a cache with stars.npy "
                        "(--prepare writes it; --prepare-stars adds it to an existing cache). Never "
                        "tune this on the gate: that is a second selection on the same session")
    p.add_argument("--star-loss-empty", action="store_true",
                   help="E2.8b arm N: add the star term's counterpart, the same ratios over windows where "
                        "the clean target has NO star (empty.npy, written by --prepare-stars), each "
                        "weighted as a star. E2.8's term without it sharpened noise into 34 to 45x the "
                        "truth's stars on the observer session")
    p.add_argument("--star-loss-refix", type=int, default=0,
                   help="E2.8b arm W: re-match the star weight to the pixel term ONCE at this step and "
                        "fix it again (0 = never). The first batch's pixel term is the injected noise, so "
                        "the weight matched there is tens of times too large once the noise is gone")
    p.add_argument("--star-max", type=int, default=32,
                   help="star targets kept per tile for stars.npy (evenly spaced in peak rank when a "
                        "tile has more; the faint end is the population L2 trades away)")
    p.add_argument("--prepare-stars", action="store_true",
                   help="write stars.npy for an EXISTING cache without touching its tiles, so an arm "
                        "can add the star term while staying paired against runs on the same cache")
    p.add_argument("--bake", default=None,
                   help="the BAKE the degradation export was cut from (its session-masters/ holds the "
                        "retained masters). --prepare needs it to recompute each session's stretch "
                        "parameters for the E3 operator, which deconvolves in LINEAR units; they are "
                        "proved against the cache's own clean tiles before stretch.npy is written")
    p.add_argument("--operator-only", action="store_true",
                   help="E3.0: run the parameter-free Richardson-Lucy operator with each tile's estimated "
                        "kernel through the deconvolution gate ONCE and print the pre-registered verdict. "
                        "Needs a cache prepared with --bake from an --estimate-kernels export")
    p.add_argument("--operator", choices=("none", "rl"), default="none",
                   help="E3.1: train the unrolled Richardson-Lucy operator with a residual U-Net prior "
                        "between iterations instead of a pixel-domain U-Net. Needs --cond-psf01 and "
                        "--synthetic, and a cache prepared with --bake (kernels.npy + stretch.npy)")
    p.add_argument("--prior-base", type=int, default=16,
                   help="channel width of the prior U-Net inside the operator (the pixel-domain arms "
                        "used --base 32; the prior runs K times per step, so it is kept smaller)")
    p.add_argument("--no-recompute", action="store_false", dest="recompute",
                   help="operator: HOLD every Richardson-Lucy iteration's activations instead of re-running "
                        "them in the backward pass. Measured on the 1070 at batch 8, K = 20, base-16 prior: "
                        "held, 11.1 GB and 31.8 s a step (past the card, paging); recomputed, 0.76 GB and "
                        "3.7 s. Only for a card with the memory")
    p.add_argument("--prior-every", type=int, default=1,
                   help="apply the prior after every Nth Richardson-Lucy iteration (1 = between every pair)")
    p.add_argument("--rl-k", type=int, default=20,
                   help="Richardson-Lucy iterations inside the operator (E3.0 pre-registers 20). On noisy "
                        "data the count IS the regulariser, so a different value is a different arm")
    p.add_argument("--out", default="n2n.pt")
    p.add_argument("--out-final", default=None,
                   help="where the LAST step's weights go when a gate selected an earlier one "
                        "(default: <out> with _final before the extension)")
    p.add_argument("--gate-every", type=int, default=0,
                   help="probe the selection metrics every N steps (0 = off, and then the last "
                        "step is what gets saved)")
    p.add_argument("--gate-cells", type=int, default=64)
    p.add_argument("--gate-sessions", type=int, default=1,
                   help="how many val sessions the gate may see; the rest stay clean for the "
                        "report, since selecting on a measurement spends its held-out-ness")
    # ON by default, at ~7% throughput (45.6 against 48.6 tiles/s), because the gate's verdict turns
    # out to depend on WHICH session it probes in a way that is otherwise invisible. Measured over
    # three runs and 19 gate-passing steps, the fabrication bar rejected NOTHING on the second
    # session (every model reads more pure there, -0.6 to -2.2 over its floor, against +3.2 to +5.9
    # on the probed one), so the probed session is the STRICTER of the two and the gate is
    # conservative by luck of the val ordering -- which `choose()` sets with a seeded shuffle. Had
    # the order come out the other way the same constants would have been systematically permissive.
    # Printing the second session makes that assumption checkable instead of implicit.
    p.add_argument("--no-gate-observe", action="store_false", dest="gate_observe",
                   help="stop probing the val sessions the gate does not select on. They are probed "
                        "by default and printed as 'obs' rows on the same schedule; observation "
                        "costs no held-out-ness, since spending it requires SELECTING on the "
                        "measurement. Do NOT gate on them as well: the noise bar shifts ~0.08 "
                        "between sessions, so demanding one absolute noise figure on both is a "
                        "silent tightening by the session shift rather than a portability fix.")
    p.add_argument("--gate-max-spurious", type=float, default=6.0,
                   help="reject a probe inventing more than this many point sources per tile "
                        "OVER the raw sub's own floor")
    p.add_argument("--cond-psf01", action="store_true",
                   help="condition on the STORED psf01 label from degradations.jsonl instead of on "
                        "the input's measured noise, and select on width and ringing instead of on "
                        "noise. This is what makes a DECONVOLUTION arm possible: the denoiser's gate "
                        "picks whichever checkpoint irons the frame flattest, which for this job is "
                        "selecting for blurring. Requires a cache prepared from a blur-mode export.")
    p.add_argument("--gate-min-stars-frac", type=float, default=0.95,
                   help="deconvolution gate only: the star floor, as a fraction of the INPUT's own "
                        "measured retention rather than of the truth's count. Renamed from "
                        "--gate-min-stars-kept, which took an absolute 0.90 and was unreachable: the "
                        "blur is what erases the faint stars, so on this project's cache the input "
                        "itself scores 0.763 and a model reproducing it exactly failed. Six seeds "
                        "and 240 probes failed that way before the null was measured. Keep this at "
                        "or just under 1.0: it is jitter allowance against the input, not a target.")
    p.add_argument("--gate-max-stars-kept", type=float, default=1.10,
                   help="deconvolution gate only, and the load-bearing half: reject a probe that has "
                        "INVENTED stars. A deconvolution cannot legitimately create a star the clean "
                        "master does not have, and sharpened noise reads to a detector as narrow "
                        "stars, which is the failure that flatters every other number. The 1.10 is a "
                        "tolerance for detection jitter and is not itself measured; what is measured "
                        "is that an unbounded version passes a model producing ten times the truth's "
                        "detections, seen on this gate's first smoke run.")
    p.add_argument("--gate-max-noise", type=float, default=0.82,
                   help="reject a probe that does not clean this hard, so a near-identity cannot "
                        "win by being the only thing pure enough to pass. Needs headroom: the "
                        "same weights read 0.04-0.17 HIGHER on a second held-out session, always "
                        "in that direction, so a value chosen on the gate session flatters itself")
    p.add_argument("--gate-max-resid", type=float, default=0.0,
                   help="report-only threshold on |residual correlation|; 0 disables it and that "
                        "is the default because the metric DOES NOT TRANSFER between sessions. "
                        "Measured across two held-out sessions, one checkpoint moved 0.301 "
                        "(+0.223 to -0.078) while the spread across six different checkpoints on "
                        "one session was 0.160, so its session shift is twice its model signal. "
                        "It was the binding gate at 0.20, where it rejected 117 of 120 probes and "
                        "was the sole reason 39 times against a 5th percentile of 0.229; the one "
                        "pass landed at 0.199 by luck. Relaxing it to 0.30 was the wrong repair. "
                        "Keep reporting it, do not decide on it")
    p.add_argument("--gate-min-faint-amp", type=float, default=0.60,
                   help="reject a probe keeping less than this fraction of faint (master SNR "
                        "8-15) star amplitude. Deliberately permissive: it bounds what the "
                        "cleaning may cost, while the objective is to clean as hard as possible")
    p.add_argument("--sigma", type=float, default=0.8)
    p.add_argument("--png", action="store_true")
    p.add_argument("--png-cells", type=int, default=4)
    a = p.parse_args()
    if a.prepare and not a.root:
        p.error("--prepare needs --root, the bake to read tiles from (no default; see --root)")
    if a.prepare:
        prepare(a)
    if a.prepare_stars and not a.prepare:
        prepare_stars(a.cache, a.star_max)
    if a.operator_only:
        operator_only(a)
    if a.train:
        train(a)
    if a.eval:
        evaluate(a)
