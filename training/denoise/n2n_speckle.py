"""The speckle teacher: `StarlessSpeckles` (C#) as a one-sided, differentiable penalty.

docs/plans/star-remover-training.md, "The speckle teacher". A speckle is a core pixel left more than 4 sigma below the
plate's own sky where a star was: a star subtracted a fraction of a pixel off leaves a dark pixel beside a bright one,
and a core MEAN, which the hole test reads, stays at the sky. The C# measure is the gate; this is the same test as a loss,
so the teacher and the gate cannot drift (`test_n2n_speckle.py` pins them per site against a fixture the C# writes).

The test at a site (cx, cy), on a luminance plane:

- the sky is the plane's pixels from 6 px (inclusive) to 12 px (exclusive) from (cx, cy), present (not absent) and finite,
  read in the window of round(cx) +- 12 px; a site whose window leaves the frame, or whose ring holds fewer than 50
  pixels, is not read;
- median is the UPPER median of the ring (sorted[n // 2]) and MAD the same rank of |ring - median| (the C# selects by rank,
  it does not average the two middle values);
- sigma = min(1.4826 * MAD, cap[round(cy), round(cx)]), `cap` being the pixels' own noise (`noise_cap`, a port of
  `PointSourceFinder.CapByDifferenceNoise`: on a bright nebula the ring's MAD is its texture, and against it the dots a
  remover digs there read as nothing); a site with no positive sigma is not read;
- a core pixel (within 3 px of (cx, cy), present, finite) is dark when it is under median - 4 sigma; the site is speckled
  when any core pixel is.

The penalty is the sum over every core pixel of relu(median - 4 sigma - pixel)^2: zero where nothing falls short, and it
needs no truth, so it applies at real stars too.

Design choices the C# does not make:

- `detach_stats=True` (default): the ring's median and sigma are computed FROM THE OUTPUT, as the plan says, but carry no
  gradient. With the gradient the net could shrink the penalty by making the ring noisier (a larger MAD is a lower limit)
  or by lowering the sky, neither of which is what the term is for; detached, it pushes on the core pixels alone.
  Pass False to read the statistics' own gradient.
- The cap is read from a plane you pass (`noise_cap(reference)`): the C# measure caps against the plate it measures. In
  training the plate is the output, whose differences a remover can bend, so cap on the INPUT (or the target), once per
  tile, never inside the loss. `noise_cap` is a Python loop over cells: compute it in the data pipeline, not per step.

Everything is batched over tiles [B, H, W] and sites [N, 3] = (tile, x, y), CPU or GPU.
"""
import math

import torch

SPECKLE_SIGMA = 4.0
CORE_RADIUS = 3.0
SKY_INNER = 6.0
SKY_OUTER = 12.0
MIN_SKY_PIXELS = 50
CONFUSION_SAFETY = 1.15
CAP_BLOCK = int(round(2 * SKY_OUTER))
MAD_TO_SIGMA = 1.4826


def _upper_median_mad(values):
    """(median, mad) of a 1-D tensor by the C# rank rule: sorted[n // 2] of the values, then of |values - median|."""
    n = values.numel()
    k = n // 2
    median = values.sort().values[k]
    mad = (values - median).abs().sort().values[k]
    return median, mad


def _robust_sigma(values):
    """`PointSourceFinder.RobustSigma`: 1.4826f x MAD over every fourth value, NaN under 16 of them."""
    values = values[::4]
    if values.numel() < 16:
        return float("nan")
    return float(torch.tensor(MAD_TO_SIGMA, dtype=torch.float32) * _upper_median_mad(values)[1])


def _difference_noise(plane, absent):
    """`PointSourceFinder.DifferenceNoise`: (pixel sigma, correlation width) from the lag-1 and lag-2 difference spreads of
    every second row, NaN for both when they do not fit the correlated-noise model. `plane` is [H, W] float32."""
    h, w = plane.shape
    a, b, c = plane[::2, : w - 2], plane[::2, 1 : w - 1], plane[::2, 2:]
    ok = torch.isfinite(a) & torch.isfinite(b) & torch.isfinite(c)
    if absent is not None:
        ab = absent[::2]
        ok &= ~ab[:, : w - 2] & ~ab[:, 1 : w - 1] & ~ab[:, 2:]
    lag1 = (b - a)[ok]
    lag2 = (c - a)[ok]
    if lag1.numel() < 1000:
        return float("nan"), float("nan")
    d1 = _robust_sigma(lag1)
    d2 = _robust_sigma(lag2)
    if not (d1 > 0.0) or not (d2 > 0.0):
        return float("nan"), float("nan")
    q = d2 * d2 / (d1 * d1)

    def rho(k, s):
        return math.exp(-k * k / (4.0 * s * s))

    def ratio(s):
        return (1.0 - rho(2, s)) / (1.0 - rho(1, s))

    lo, hi = 0.05, 20.0
    if not (q > ratio(lo)) or not (q < ratio(hi)):
        return float("nan"), float("nan")
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        if ratio(mid) < q:
            lo = mid
        else:
            hi = mid
    width = 0.5 * (lo + hi)
    return math.sqrt(d1 * d1 / (2.0 * (1.0 - rho(1, width)))), width


def _difference_noise_map(plane, absent, block):
    """`PointSourceFinder.DifferenceNoiseMap`: the pixel noise per cell of `block` pixels (each cell's lag-1 difference spread
    scaled by the correlation width the whole plane gives), bilinear between cell centres; None when the model does not fit.
    `plane` is [H, W] float32."""
    h, w = plane.shape
    pixel_sigma, width = _difference_noise(plane, absent)
    if not (pixel_sigma > 0) or not (width > 0):
        return None
    scale = 1.0 / math.sqrt(2.0 * (1.0 - math.exp(-1.0 / (4.0 * width * width))))
    cells_x = (w + block - 1) // block
    cells_y = (h + block - 1) // block
    present = torch.isfinite(plane)
    if absent is not None:
        present &= ~absent
    # A difference counts where both its pixels are present; the one to the right / below may lie in the next cell.
    dx = plane[:, 1:] - plane[:, :-1]
    okx = present[:, 1:] & present[:, :-1]
    dy = plane[1:, :] - plane[:-1, :]
    oky = present[1:, :] & present[:-1, :]
    cells = torch.full((cells_y, cells_x), float("nan"), dtype=torch.float64)
    for cy in range(cells_y):
        for cx in range(cells_x):
            y0, y1 = cy * block, min(h, (cy + 1) * block)
            x0, x1 = cx * block, min(w, (cx + 1) * block)
            sx = (slice(y0, y1), slice(x0, min(x1, w - 1)))
            sy = (slice(y0, min(y1, h - 1)), slice(x0, x1))
            diffs = torch.cat([dx[sx][okx[sx]], dy[sy][oky[sy]]])
            if diffs.numel() >= 32:
                cells[cy, cx] = scale * float(MAD_TO_SIGMA * _upper_median_mad(diffs)[1])
    cells = torch.where(cells > 0, cells, torch.full_like(cells, pixel_sigma))

    def axis(n, cells_n):
        f = ((torch.arange(n, dtype=torch.float64) + 0.5) / block - 0.5).clamp(0.0, cells_n - 1.0)
        i0 = f.floor().long()
        return i0, (i0 + 1).clamp(max=cells_n - 1), f - i0

    y0i, y1i, ty = axis(h, cells_y)
    x0i, x1i, tx = axis(w, cells_x)
    ty, tx = ty[:, None], tx[None, :]
    top = (1 - tx) * cells[y0i][:, x0i] + tx * cells[y0i][:, x1i]
    bottom = (1 - tx) * cells[y1i][:, x0i] + tx * cells[y1i][:, x1i]
    return ((1 - ty) * top + ty * bottom).float()


@torch.no_grad()
def noise_cap(plane, absent=None, block=CAP_BLOCK):
    """The cap on a ring's MAD per pixel, `StarlessSpeckles.NoiseCap`: CapByDifferenceNoise on a plane of +inf, i.e.
    1.15 x the pixels' own difference noise where it is positive, +inf where the plane's differences do not fit the model.

    `plane` is [B, H, W] (or [H, W]) float32, `absent` a bool plane of the same shape (True = never read). Returns the same
    shape, float32. Compute it once per tile on the input, not per step (a loop over cells)."""
    single = plane.dim() == 2
    plane = plane.float()[None] if single else plane.float()
    if absent is not None:
        absent = absent[None] if single else absent
    out = torch.full_like(plane, float("inf"))
    for i in range(plane.shape[0]):
        local = _difference_noise_map(plane[i], None if absent is None else absent[i], block)
        if local is None:
            continue
        cap = (CONFUSION_SAFETY * local.double()).float()
        out[i] = torch.where((cap > 0) & (cap < out[i]), cap, out[i])
    return out[0] if single else out


class SiteReads:
    """What `read_sites` found at each of N sites: `valid` (the site was read), `speckled` (valid and a core pixel is
    dark), `median` and `sigma` of the ring (NaN where not valid) and `penalty` per site (zero where not valid)."""

    def __init__(self, valid, speckled, median, sigma, penalty):
        self.valid, self.speckled, self.median, self.sigma, self.penalty = valid, speckled, median, sigma, penalty


def read_sites(plane, sites, cap, absent=None, detach_stats=True):
    """The speckle test at every site, batched.

    plane: [B, H, W] float32, the plate under test (the remover's output on a luminance), requires_grad allowed.
    sites: [N, 3] (tile index, x, y), the centre sub-pixel, pixel centres at integers (the C# convention).
    cap: [B, H, W] from `noise_cap`. absent: [B, H, W] bool or None.
    """
    b, h, w = plane.shape
    dev = plane.device
    sites = torch.as_tensor(sites, dtype=torch.float64, device=dev).reshape(-1, 3)
    n = sites.shape[0]
    tile = sites[:, 0].long()
    cx, cy = sites[:, 1], sites[:, 2]
    x0 = torch.round(cx).long()  # half to even, as Math.Round
    y0 = torch.round(cy).long()
    r = int(math.ceil(SKY_OUTER))
    inside = (x0 - r >= 0) & (y0 - r >= 0) & (x0 + r < w) & (y0 + r < h)

    off = torch.arange(-r, r + 1, device=dev)
    yy = (y0[:, None, None] + off[None, :, None]).clamp(0, h - 1).expand(n, 2 * r + 1, 2 * r + 1)
    xx = (x0[:, None, None] + off[None, None, :]).clamp(0, w - 1).expand(n, 2 * r + 1, 2 * r + 1)
    win = plane[tile[:, None, None], yy, xx]
    d2 = (
        ((x0[:, None, None] + off[None, None, :]).double() - cx[:, None, None]) ** 2
        + ((y0[:, None, None] + off[None, :, None]).double() - cy[:, None, None]) ** 2
    )
    present = torch.isfinite(win)
    if absent is not None:
        present = present & ~absent[tile[:, None, None], yy, xx]

    ring = present & (d2 >= SKY_INNER * SKY_INNER) & (d2 < SKY_OUTER * SKY_OUTER)
    count = ring.flatten(1).sum(1)
    enough = inside & (count >= MIN_SKY_PIXELS)
    k = (count // 2).clamp(min=0)[:, None]
    inf = torch.full_like(win, float("inf"))
    sky = torch.where(ring, win, inf).flatten(1)
    median = sky.sort(1).values.gather(1, k)[:, 0]
    median = torch.where(enough, median, torch.zeros_like(median))
    dev_abs = torch.where(ring, (win - median[:, None, None]).abs(), inf).flatten(1)
    mad = dev_abs.sort(1).values.gather(1, k)[:, 0]
    mad = torch.where(enough, mad, torch.zeros_like(mad))
    site_cap = cap[tile, y0.clamp(0, h - 1), x0.clamp(0, w - 1)]
    sigma = torch.minimum(torch.tensor(MAD_TO_SIGMA, dtype=plane.dtype, device=dev) * mad, site_cap)
    valid = enough & (sigma > 0)
    sigma = torch.where(valid, sigma, torch.zeros_like(sigma))
    if detach_stats:
        median, sigma = median.detach(), sigma.detach()

    limit = median - SPECKLE_SIGMA * sigma
    c = int(math.ceil(CORE_RADIUS))
    centre = slice(r - c, r + c + 1)
    core = present[:, centre, centre] & (d2[:, centre, centre] <= CORE_RADIUS * CORE_RADIUS) & valid[:, None, None]
    core_values = win[:, centre, centre]
    short = torch.relu(limit[:, None, None] - core_values)
    penalty = torch.where(core, short * short, torch.zeros_like(short)).flatten(1).sum(1)
    dark = (core & (core_values < limit[:, None, None])).flatten(1).any(1)

    nan = torch.full_like(median, float("nan"))
    return SiteReads(valid, dark, torch.where(valid, median, nan), torch.where(valid, sigma, nan), penalty)


def speckle_penalty(plane, sites, cap, absent=None, detach_stats=True):
    """The loss term: the summed shortfall penalty over every site, a scalar (divide by the site count to mean it)."""
    return read_sites(plane, sites, cap, absent, detach_stats).penalty.sum()


# Where the trainer's sites come from (--speckle-weight in n2n_smoke.py, #1362). A removed star's site is where the draw's
# own star layer peaks: the layer is the draw less its truth (the star the draw injected), read in the draw's own noise
# sigma, so a site is a local maximum of that layer at or above a significance floor. The same planes --bright-weight reads
# (the stored per-pixel noise plane), so the option needs them too. A flat saturated core peaks on many equal pixels, so a
# tile keeps at most MAX_SITES_PER_TILE of its brightest peaks, ties broken by the lowest index (deterministic).
PEAK_RADIUS = 2
MAX_SITES_PER_TILE = 32


def peak_sites(layer, floor, margin, max_per_tile=MAX_SITES_PER_TILE):
    """The sites [N, 3] (tile, x, y), float64, of `layer` [B, 1, H, W] (the star layer in noise sigmas): the pixels at or
    above `floor` that are the maximum of their (2 * PEAK_RADIUS + 1)^2 neighbourhood and at least `margin` px from every
    edge (a site closer reads no ring), brightest first within a tile, at most `max_per_tile` of them per tile."""
    b, _, h, w = layer.shape
    z = layer[:, 0]
    peak = (z >= floor) & (z == torch.nn.functional.max_pool2d(layer, 2 * PEAK_RADIUS + 1, 1, PEAK_RADIUS)[:, 0])
    inner = torch.zeros_like(peak)
    inner[:, margin : h - margin, margin : w - margin] = True
    score = torch.where(peak & inner, z, torch.full_like(z, float("-inf"))).flatten(1)
    values, index = score.topk(min(max_per_tile, score.shape[1]), dim=1)
    keep = torch.isfinite(values)
    tile = torch.arange(b, device=layer.device)[:, None].expand_as(index)[keep]
    index = index[keep]
    return torch.stack([tile, index % w, index // w], dim=1).double()


@torch.no_grad()
def tile_cap(luminance):
    """`noise_cap` of the INPUT luminance [B, H, W], once per tile (see the module docstring on why not the output's)."""
    return noise_cap(luminance.float())


def site_penalty(output, luminance_in, layer, floor, border):
    """The trainer's term: `speckle_penalty` of the output's luminance (the mean of its channels) at the peaks of `layer`,
    for sites whose core lies inside the `border`-px rim the pixel term leaves out, over the pixels that term averages (the
    batch's tile less the rim), i.e. in the pixel term's own units, so a weight of 1 is the same order as the plain
    objective. `output` [B, C, H, W] carries the gradient; `luminance_in` [B, H, W] is the draw the net was fed. A batch
    with no site gives a zero that still belongs to the graph."""
    b, _, h, w = output.shape
    sites = peak_sites(layer, floor, border + int(math.ceil(CORE_RADIUS)))
    if sites.shape[0] == 0:
        return output.sum() * 0.0
    return speckle_penalty(output.mean(dim=1), sites, tile_cap(luminance_in)) / (b * (h - 2 * border) * (w - 2 * border))
