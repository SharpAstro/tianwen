"""R9 (#1056): a learned refiner on the shipped planetary sharpening, trained on `tianwen planetary training-pairs`.

The net adds a residual to the derived-gains master D: output = D + net(D, S, rho). Inputs are normalised as a real master would be at
deploy time (the sky the median past 1.4 radii of the stack, the level the stack's mean inside 0.5 radii above it), so no training-only
fact reaches the input. The loss is L2 against the truth (through the pupil, the sharpening's own target) inside 1.05 radii.

  python r9_refiner.py train --pairs DIR --held DIR --out DIR [--steps 8000] [--base 32]
  python r9_refiner.py apply --model DIR/model.pt --pairs DIR --out DIR      (writes <index>_refined.fits beside nothing else)
  python r9_refiner.py one --model DIR/model.pt --stack M.fits --sharp M_sharpened.fits --disk X,Y,R --out OUT.fits
      (one master: its disk from a truth's DISKX/Y/R or the limb fit; a colour master is refined a channel at a time)

Rules, gates and results: docs/plans/planetary-restoration.md, "R9 revisited" (python is glue; the pairs and every score are the CLI's).
"""
import argparse, csv, json, math, os, random, time
import numpy as np
from astropy.io import fits

CROP = 128


def load_pair(folder, index):
    stem = os.path.join(folder, f"{int(index):05d}")
    s = fits.getdata(stem + "_stack.fits").astype(np.float32)
    d = fits.getdata(stem + "_sharp.fits").astype(np.float32)
    h = fits.getheader(stem + "_truth.fits")
    t = fits.getdata(stem + "_truth.fits").astype(np.float32)
    return s, d, t, (float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"]))


def normalise(s, d, disk, t=None):
    """The deploy-time normalisation: sky and level from the stack and the disk alone."""
    x0, y0, r = disk
    yy, xx = np.indices(s.shape)
    rho = np.hypot(xx - x0, yy - y0) / r
    sky = float(np.median(s[rho > 1.4])) if (rho > 1.4).sum() > 50 else float(np.percentile(s, 5))
    level = float(s[rho < 0.5].mean() - sky)
    planes = np.stack([(d - sky) / level, (s - sky) / level, np.minimum(rho, 2.0).astype(np.float32)])
    target = None
    if t is not None:
        # The truth over ITS OWN sky and level, as the scorer normalises it (PlanetaryMetrics.Normalise): a synthetic pair's truth is in
        # the stack's units, a degrade twin's in the photons it was rendered in, about 200 times a master's [0, 1] scale.
        t_sky = float(np.median(t[rho > 1.4])) if (rho > 1.4).sum() > 50 else 0.0
        target = ((t - t_sky) / float(t[rho < 0.5].mean() - t_sky))[None].astype(np.float32)
    return planes.astype(np.float32), target, rho, sky, level


def pairs_in(folder):
    rows = []
    if not os.path.exists(os.path.join(folder, "pairs.csv")):
        # A set still being made: its pairs by their truths on disk (the CSV is written at the end).
        return sorted(name[:5] for name in os.listdir(folder) if name.endswith("_truth.fits"))
    with open(os.path.join(folder, "pairs.csv")) as f:
        for row in csv.DictReader(f):
            if row.get("size"):
                rows.append(row["index"])
    return rows


def build_model(base):
    import torch
    import torch.nn as nn

    def block(cin, cout):
        return nn.Sequential(nn.Conv2d(cin, cout, 3, padding=1), nn.LeakyReLU(0.1, inplace=True),
                             nn.Conv2d(cout, cout, 3, padding=1), nn.LeakyReLU(0.1, inplace=True))

    class Refiner(nn.Module):
        """The N2N trainer's small U-Net, three inputs, a residual onto the first (D)."""

        def __init__(self):
            super().__init__()
            self.e1, self.e2, self.e3 = block(3, base), block(base, base * 2), block(base * 2, base * 4)
            self.mid = block(base * 4, base * 4)
            self.u3 = nn.Sequential(nn.Upsample(scale_factor=2, mode="nearest"), nn.Conv2d(base * 4, base * 2, 3, padding=1))
            self.d3 = block(base * 4, base * 2)
            self.u2 = nn.Sequential(nn.Upsample(scale_factor=2, mode="nearest"), nn.Conv2d(base * 2, base, 3, padding=1))
            self.d2 = block(base * 2, base)
            self.out = nn.Conv2d(base, 1, 1)
            nn.init.zeros_(self.out.weight)
            nn.init.zeros_(self.out.bias)
            self.pool = nn.MaxPool2d(2)

        def forward(self, x):
            e1 = self.e1(x)
            e2 = self.e2(self.pool(e1))
            e3 = self.e3(self.pool(e2))
            m = self.mid(e3)
            d3 = self.d3(torch.cat([self.u3(m), e2], 1))
            d2 = self.d2(torch.cat([self.u2(d3), e1], 1))
            return x[:, :1] + self.out(d2)

    return Refiner()


def infer(model, planes, device):
    """The whole image through the net, padded to a multiple of 4 by reflection; D kept outside the limb.

    The loss covered 1.05 radii only, so past it the net's output is unconstrained: the result is D outside 1.05 radii, the net inside
    0.95, a smoothstep between (RULES.md, 2026-10-13: an unconstrained sky rescaled every band by the scorer's normalisation)."""
    import torch
    c, h, w = planes.shape
    ph, pw = (-h) % 4, (-w) % 4
    padded = np.pad(planes, ((0, 0), (0, ph), (0, pw)), mode="reflect")
    with torch.no_grad():
        out = model(torch.from_numpy(padded[None]).to(device))
    net = out[0, 0, :h, :w].cpu().numpy()
    t = np.clip((1.05 - planes[2]) / 0.10, 0.0, 1.0)
    weight = t * t * (3 - 2 * t)
    return weight * net + (1 - weight) * planes[0]


def train(args):
    import torch
    torch.manual_seed(args.seed)
    random.seed(args.seed)
    np.random.seed(args.seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ids = pairs_in(args.pairs)
    held = pairs_in(args.held)[: args.val]
    print(f"device {device}, {len(ids)} training pairs, {len(held)} held out for the curve", flush=True)
    data = []
    for i in ids:
        s, d, t, disk = load_pair(args.pairs, i)
        planes, target, rho, _, _ = normalise(s, d, disk, t)
        data.append((planes, target, disk))
    val = []
    for i in held:
        s, d, t, disk = load_pair(args.held, i)
        planes, target, rho, _, _ = normalise(s, d, disk, t)
        val.append((planes, target, rho))
    model = build_model(args.base).to(device)
    if args.init:
        model.load_state_dict(torch.load(args.init, map_location=device)["state"])
        print(f"started from {args.init}", flush=True)
    params = sum(p.numel() for p in model.parameters())
    print(f"refiner base {args.base}, {params / 1e6:.2f} M parameters", flush=True)
    opt = torch.optim.Adam(model.parameters(), lr=args.lr)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.steps)
    log = []
    t0 = time.time()

    def batch():
        xs, ys, ms = [], [], []
        for _ in range(args.batch):
            planes, target, disk = data[random.randrange(len(data))]
            _, h, w = planes.shape
            x0, y0, r = disk
            # A crop centred within 1.2 radii of the disk's centre: the disk and its limb, never only sky.
            ang, rad = random.random() * 2 * math.pi, random.random() * 1.2 * r
            cx = int(np.clip(x0 + rad * math.cos(ang) - CROP / 2, 0, w - CROP))
            cy = int(np.clip(y0 + rad * math.sin(ang) - CROP / 2, 0, h - CROP))
            x = planes[:, cy:cy + CROP, cx:cx + CROP]
            y = target[:, cy:cy + CROP, cx:cx + CROP]
            k = random.randrange(4)
            x, y = np.rot90(x, k, axes=(1, 2)), np.rot90(y, k, axes=(1, 2))
            if random.random() < 0.5:
                x, y = x[:, :, ::-1], y[:, :, ::-1]
            m = (x[2] < 1.05).astype(np.float32)[None]
            xs.append(np.ascontiguousarray(x)); ys.append(np.ascontiguousarray(y)); ms.append(m)
        to = lambda a: torch.from_numpy(np.stack(a)).to(device)
        return to(xs), to(ys), to(ms)

    def evaluate():
        model.eval()
        net, base = 0.0, 0.0
        for planes, target, rho in val:
            out = infer(model, planes, device)
            m = rho < 0.9
            net += float(np.mean((out[m] - target[0][m]) ** 2))
            base += float(np.mean((planes[0][m] - target[0][m]) ** 2))
        model.train()
        return net / len(val), base / len(val)

    for step in range(1, args.steps + 1):
        x, y, m = batch()
        out = model(x)
        loss = (((out - y) ** 2) * m).sum() / m.sum().clamp(min=1)
        opt.zero_grad()
        loss.backward()
        opt.step()
        sched.step()
        if step % args.every == 0 or step == args.steps:
            net, base = evaluate()
            row = {"step": step, "loss": loss.item(), "held_mse": net, "held_mse_D": base, "ratio": net / base, "seconds": round(time.time() - t0)}
            log.append(row)
            print(json.dumps(row), flush=True)
    os.makedirs(args.out, exist_ok=True)
    torch.save({"state": model.state_dict(), "base": args.base}, os.path.join(args.out, "model.pt"))
    with open(os.path.join(args.out, "curve.json"), "w") as f:
        json.dump(log, f, indent=1)


def apply(args):
    import torch
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(args.model, map_location=device)
    model = build_model(ck["base"]).to(device)
    model.load_state_dict(ck["state"])
    model.eval()
    os.makedirs(args.out, exist_ok=True)
    for i in pairs_in(args.pairs):
        s, d, t, disk = load_pair(args.pairs, i)
        planes, _, _, sky, level = normalise(s, d, disk)
        out = infer(model, planes, device) * level + sky
        hdr = fits.getheader(os.path.join(args.pairs, f"{int(i):05d}_sharp.fits"))
        fits.writeto(os.path.join(args.out, f"{int(i):05d}_refined.fits"), out.astype(np.float32), hdr, overwrite=True)
    print(f"refined {len(pairs_in(args.pairs))} pairs into {args.out}")


def one(args):
    import torch
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(args.model, map_location=device)
    model = build_model(ck["base"]).to(device)
    model.load_state_dict(ck["state"])
    model.eval()
    disk = tuple(float(v) for v in args.disk.split(","))
    s_all = fits.getdata(args.stack).astype(np.float32)
    d_all = fits.getdata(args.sharp).astype(np.float32)
    hdr = fits.getheader(args.sharp)
    colour = s_all.ndim == 3
    planes_out = []
    for c in range(s_all.shape[0] if colour else 1):
        s, d = (s_all[c], d_all[c]) if colour else (s_all, d_all)
        planes, _, _, sky, level = normalise(s, d, disk)
        planes_out.append(infer(model, planes, device) * level + sky)
    out = np.stack(planes_out) if colour else planes_out[0]
    fits.writeto(args.out, out.astype(np.float32), hdr, overwrite=True)
    print(f"refined {args.sharp} into {args.out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("train")
    t.add_argument("--pairs", required=True); t.add_argument("--held", required=True); t.add_argument("--out", required=True)
    t.add_argument("--steps", type=int, default=8000); t.add_argument("--batch", type=int, default=16); t.add_argument("--lr", type=float, default=2e-4)
    t.add_argument("--base", type=int, default=32); t.add_argument("--seed", type=int, default=1); t.add_argument("--val", type=int, default=40)
    t.add_argument("--every", type=int, default=1000)
    t.add_argument("--init", default=None, help="weights to start from (a model.pt), else a fresh net (the identity)")
    a = sub.add_parser("apply")
    a.add_argument("--model", required=True); a.add_argument("--pairs", required=True); a.add_argument("--out", required=True)
    o = sub.add_parser("one")
    o.add_argument("--model", required=True); o.add_argument("--stack", required=True); o.add_argument("--sharp", required=True)
    o.add_argument("--disk", required=True); o.add_argument("--out", required=True)
    args = ap.parse_args()
    {"train": train, "apply": apply, "one": one}[args.cmd](args)
