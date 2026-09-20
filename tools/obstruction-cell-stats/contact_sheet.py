"""Contact sheet, with the downsample fixed.

The first one took every Nth pixel. A star is 2 to 3 px across, so decimation by 5 skips most of
them and every panel came back as pure noise, which is a property of the resampler and not of the
data. Block MAX instead: it keeps a point source in the tile it falls in, which is the whole reason
to look.
"""
import glob, os, sys, warnings
import numpy as np
warnings.filterwarnings("ignore")
from astropy.io import fits
from PIL import Image, ImageDraw

D, COLS, TILE = sys.argv[1], 6, 260
fs = sorted(glob.glob(os.path.join(D, "*.fits")))
rows = (len(fs) + COLS - 1) // COLS
sheet = Image.new("L", (COLS * TILE, rows * TILE), 20)
draw = ImageDraw.Draw(sheet)
for i, f in enumerate(fs):
    with fits.open(f, memmap=False) as h:
        a = h[0].data[0::2, 1::2].astype(np.float32)
        t = str(h[0].header.get("DATE-OBS", ""))[11:19]
    k = max(1, min(a.shape) // TILE)
    hh, ww = (a.shape[0] // k) * k, (a.shape[1] // k) * k
    blocks = a[:hh, :ww].reshape(hh // k, k, ww // k, k)
    peak = blocks.max(axis=(1, 3))[:TILE, :TILE]      # keeps point sources
    lo = np.percentile(peak, 20)
    hi = np.percentile(peak, 99.8)
    v = np.clip((peak - lo) / max(hi - lo, 1e-6), 0, 1) ** 0.45
    img = Image.fromarray((v * 255).astype(np.uint8)).resize((TILE, TILE))
    x, y = (i % COLS) * TILE, (i // COLS) * TILE
    sheet.paste(img, (x, y))
    draw.rectangle([x, y, x + TILE - 1, y + TILE - 1], outline=90)
    draw.text((x + 5, y + 5), f"{i+1}  {t}", fill=255)
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "contact_sheet2.png")
sheet.save(out)
print(out, sheet.size)
