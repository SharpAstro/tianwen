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
    cells = defaultdict(lambda: {"subs": [], "master": None, "half_a": None, "half_b": None})
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

    def read_tile(rel):
        with open(os.path.join(args.root, rel.replace("/", os.sep)), "rb") as fh:
            raw = fh.read()
        if len(raw) != BYTES:
            raise SystemExit(f"tile {rel} is {len(raw)} bytes, expected {BYTES}")
        return np.frombuffer(raw, "<f2").reshape(CH, TILE, TILE)

    t0 = time.perf_counter()
    for i, key in enumerate(keys):
        entry = cells[key]
        paths = [entry["master"]] + sorted(entry["subs"])[:SUBS_PER_CELL]
        for slot, rel in enumerate(paths):
            mm[i, slot] = read_tile(rel)
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
    injected = all(
        os.path.basename(rel).rsplit("_", 1)[-1].startswith("deg")
        for key in keys for rel in cells[key]["subs"][:SUBS_PER_CELL])
    print(f"  sub slots: {'INJECTED draws' if injected else 'real subs'}")
    meta = {
        "cells": n, "slots": SLOTS_WITH_HALVES, "injected": bool(injected),
        "train_cells": len(train_keys), "val_cells": len(val_keys),
        "train_sessions": train_s, "val_sessions": val_s,
        "has_halves": halves,
        "keys": [[k[0], k[1], k[2]] for k in keys],
    }
    with open(os.path.join(args.cache, "meta.json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh, indent=1)
    gb = os.path.getsize(path) / 2**30
    print(f"cached {n} cells ({gb:.2f} GiB) in {(time.perf_counter()-t0)/60:.1f} min -> {path}")


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
        print("regime: synthetic (a degraded draw against the clean target in slot 0). "
              "Note the target is a MASTER, so it carries its own 1/sqrt(N) noise and the model "
              "learns to leave that; score against a held-out half, never against this target.")
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
    model = build_model(args.base, args.upsample, cond_planes).to(dev)
    params = sum(p.numel() for p in model.parameters())
    opt = torch.optim.Adam(model.parameters(), args.lr)
    print(f"device {dev}, U-Net base={args.base}, {params/1e6:.2f} M params, "
          f"{n_train} train cells, conditioning planes {cond_planes}")

    # The mid-training probe. Loss cannot select a denoiser here (it falls fastest for a model
    # that irons the frame flat, because the background is most of the pixels) and neither can
    # PSNR. So selection runs on the two measures that reversed verdicts in the smoke runs.
    gate = None
    observers = []
    if args.gate_every > 0:
        import n2n_gate                       # imported here: it imports this module back
        cells = gate_cells(meta, args.gate_sessions, args.gate_cells)
        gate = n2n_gate.Gate(mm, cells, dev)
        print(f"gate: {len(cells)} cells from {args.gate_sessions} val session(s), probing every "
              f"{args.gate_every} steps; floor {gate.floor_spurious:.1f} spurious/tile")
        if args.gate_observe:
            for s, ocells in observer_cells(meta, args.gate_sessions, args.gate_cells):
                observers.append((s, n2n_gate.Gate(mm, ocells, dev)))
                print(f"  OBSERVING (never selected on) {len(ocells)} cells from {s[:44]}; "
                      f"floor {observers[-1][1].floor_spurious:.1f} spurious/tile")
        # Names the three gates that are actually in the pass condition. It used to print the
        # residual threshold too, left behind when resid became report-only, which read as a
        # fourth gate in six runs' worth of logs.
        print(f"  pass requires spurious-over-floor <= {args.gate_max_spurious}, faint amp >= "
              f"{args.gate_min_faint_amp}, noise <= {args.gate_max_noise}x")
        print(f"  |resid corr| is REPORTED ONLY (it does not transfer between sessions)")
        print(f"  among passers the QUIETEST wins (doing nothing is 1.00x, the worst answer)")
        print(f"  step   {n2n_gate.Gate.header()}   {'obj':>6}  {'':4}")

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
    regime_steps = defaultdict(int)
    best = (-1.0, 0, None, None)      # (score, step, metrics, state_dict)
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
            # Input: one injected draw. Target: the undegraded tile every draw was made from.
            x = torch.from_numpy(np.ascontiguousarray(mm[idx, a])).to(dev).float()
            y = torch.from_numpy(np.ascontiguousarray(mm[idx, SLOT_MASTER])).to(dev).float()
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

        pred = model(with_sigma(x, planes=cond_planes) if cond_planes else x)
        # Mask the rim: at inference no output pixel comes from a chunk edge, so a loss over
        # the full tile optimises a condition the model never meets.
        pc = pred[:, :, BORDER:-BORDER, BORDER:-BORDER]
        yc = y[:, :, BORDER:-BORDER, BORDER:-BORDER]
        # L1 converges to the conditional MEDIAN, which for a star near the noise floor sits
        # at the background: an L1 N2N erases faint stars while scoring well on PSNR, because
        # PSNR is dominated by the background pixels it cleans beautifully. L2 converges to the
        # conditional MEAN, which is unbiased and preserves faint flux in expectation.
        loss = (nn.functional.l1_loss(pc, yc) if args.loss == "l1"
                else nn.functional.mse_loss(pc, yc))

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
        opt.zero_grad(set_to_none=True)
        loss.backward()
        opt.step()
        sched.step()
        running.append(loss.item())

        if step % args.log_every == 0 or step == steps:
            el = time.perf_counter() - t0
            print(f"  step {step:6d}/{steps}  loss {np.mean(running[-args.log_every:]):.5f}  "
                  f"{step*args.batch/el:5.1f} tiles/s  elapsed {el/60:5.1f} min", flush=True)

        if gate is not None and (step % args.gate_every == 0 or step == steps):
            m = gate.evaluate(model, cond_planes)
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
            passed = (m["spurious_over_floor"] <= args.gate_max_spurious
                      and m["faint_amp"] >= args.gate_min_faint_amp
                      and m["noise"] <= args.gate_max_noise)
            score = m["noise"]
            mark = "pass" if passed else "FAIL"
            if passed and (best[3] is None or score < best[0]):
                best = (score, step, m,
                        {k: v.detach().cpu().clone() for k, v in model.state_dict().items()})
                mark = "pass *"
            print(f"  gate {step:6d}   {n2n_gate.Gate.format(m)}   {score:6.3f}  {mark}",
                  flush=True)
            # The observed sessions print on the SAME schedule with an "obs" tag and no verdict
            # column, so the two trajectories are aligned step-for-step in one log and neither can
            # be mistaken for the one that selected.
            for si, (_, og) in enumerate(observers):
                om = og.evaluate(model, cond_planes)
                print(f"  obs{si} {step:6d}   {n2n_gate.Gate.format(om)}", flush=True)

    if len(regimes) > 1:
        print("  steps per regime: " + "  ".join(
            f"{k}={regime_steps[k]}" for k in regimes))

    def save(state, path, selected_at):
        torch.save({"model": state, "base": args.base, "upsample": args.upsample,
                    "cond": cond_planes, "half_pairs": args.half_pairs,
                    "regimes": [str(k) for k in regimes], "selected_at_step": selected_at,
                    "pair_time": args.pair_time},
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
        save(model.state_dict(), args.out, steps)


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
    p.add_argument("--synthetic", action="store_true",
                   help="supervised regime: train an injected draw against the CLEAN target in "
                        "slot 0, instead of one noisy view against another. Needs a cache prepared "
                        "from `tianwen dataset degrade`, and is exclusive of the N2N regimes so the "
                        "arm answers H1. The target is a master, so it carries its own 1/sqrt(N) "
                        "noise: score against a held-out half, never against the target itself")
    p.add_argument("--half-pairs", action="store_true",
                   help="train on the half-master pair as a fourth regime (needs a cache "
                        "prepared from a bake that exports halves)")
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
    if a.train:
        train(a)
    if a.eval:
        evaluate(a)
