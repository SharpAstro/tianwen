"""Build the gallery's row table from a bake store: one row per retained session master.

    python build_rows.py <bake store> <rows.json out>

Everything a card shows comes from the store itself -- the master's own FITS header for what the
session WAS, `stats/psf-sessions.jsonl` for what was measured of it -- so a gallery can be rebuilt
from a bake with no hand-kept list anywhere. The previous version of this table was assembled by
hand, which is why it went stale the moment a session was re-baked.

The 1:1 patch coordinates are chosen here, not measured by the product: a 320 px square on the
QUIETEST part of the frame, picked by the lowest 95th percentile over a coarse grid inside the
middle half of the canvas. That is a SELECTION for a picture, not imaging logic -- it decides where
to look, never what the pixels are.
"""
import json
import os
import re
import sys

import numpy as np
from astropy.io import fits

PATCH = 320


def sessions_stats(store):
    """SessionId -> the measured record, where the bake wrote one."""
    path = os.path.join(store, "stats", "psf-sessions.jsonl")
    out = {}
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if line:
                rec = json.loads(line)
                out[rec["SessionId"]] = rec          # last wins: the store is last-wins by id
    return out


def header_of(path):
    with fits.open(path, memmap=True) as h:
        for hdu in h:
            if hdu.header.get("NAXIS", 0) >= 2:
                return hdu.header
    raise ValueError("no image HDU in " + path)


def quiet_patch(path):
    """Where to take the 1:1 patch: the quietest square inside the middle half of the canvas."""
    with fits.open(path, memmap=True) as h:
        data = next(x.data for x in h if x.data is not None)
    plane = data[0] if data.ndim == 3 else data
    hgt, wid = plane.shape
    best, best_at = None, (max(0, (hgt - PATCH) // 2), max(0, (wid - PATCH) // 2))
    for y in np.linspace(hgt * 0.25, hgt * 0.75 - PATCH, 5):
        for x in np.linspace(wid * 0.25, wid * 0.75 - PATCH, 5):
            y0, x0 = int(max(0, y)), int(max(0, x))
            tile = plane[y0:y0 + PATCH, x0:x0 + PATCH]
            if tile.size == 0 or not np.isfinite(tile).any():
                continue
            score = float(np.nanpercentile(tile, 95))
            if best is None or score < best:
                best, best_at = score, (y0, x0)
    return best_at


# DatasetTileExporter.Sanitize, mirrored exactly: only the characters a path cannot hold become '_',
# and SPACES SURVIVE. A prettier slug here matches nothing -- joining the stats on a hand-rolled
# version silently produced 92 rows with no optical train and no FWHM, which reads as a bake that
# measured nothing rather than as a join that missed.
_ILLEGAL = set('/\\|:*?"<>')


def sanitize(session_id):
    return "".join("_" if c in _ILLEGAL else c for c in session_id)


# IntegrationFitsWriter.RejectionMapSuffix, mirrored. The sidecar is a .fits in the SAME folder as
# the master it belongs to, so a bare *.fits listing counts every master twice -- this table said 278
# masters for a 139-master store, with "flip-side 96" for 48 real ones. The C# side asks
# IntegrationFitsWriter.IsRejectionMapPath; there is no verb to call from here, so this is the one
# rule the glue mirrors, and it mirrors it in ONE place.
REJECTION_SUFFIX = ".rejection.fits"


def is_rejection_map(name):
    return name.lower().endswith(REJECTION_SUFFIX)


def rejection_path(master_path):
    """IntegrationFitsWriter.RejectionPathFor: the .fits stem is STRIPPED before the suffix."""
    return os.path.splitext(master_path)[0] + REJECTION_SUFFIX


def web_slug(text):
    """A url-ish token for the card anchor; never used to find a file."""
    return "".join(c if c.isalnum() or c in "-_" else "-" for c in text)


# A FLIPPED session yields THREE masters, and there are only two sides of a meridian: the
# combined one over the whole night, plus one per pier side (#45). The suffix is what the split
# writes, and the combined master is simply the same name without it -- so "there is a sibling
# called <me>_flip=a" is what identifies it, and nothing has to parse a session id to find out.
# Until now the table only said flip=true/false, so the three read as three duplicates on the
# page: same target, same night, same train, three cards (#60). They are not duplicates, and the
# sub counts prove it -- the Lobster Nebula night is 100 subs combined, 32 on one side and 68 on
# the other.
FLIP_SUFFIX = re.compile(r"_flip=([ab])$")


def flip_sides(names):
    """name -> 'a' | 'b' | 'both' | None, over the whole listing at once."""
    stems, out = set(names), {}
    for n in names:
        m = FLIP_SUFFIX.search(n)
        out[n] = m.group(1) if m else ("both" if n + "_flip=a" in stems else None)
    return out


def flip_group(name):
    """The night a split master belongs to: its own name with the side suffix removed. Shared by
    all three cards of a flipped session, so the page can say which of the set it is looking at."""
    return FLIP_SUFFIX.sub("", name)


def main():
    store, out_path = sys.argv[1], sys.argv[2]
    masters_dir = os.path.join(store, "session-masters")
    stats = sessions_stats(store)
    by_train = {}
    for rec in stats.values():
        by_train[rec["SessionId"]] = rec.get("OpticalTrain")

    rows = []
    names = sorted(f[:-5] for f in os.listdir(masters_dir)
                   if f.lower().endswith(".fits") and not is_rejection_map(f))
    sides = flip_sides(names)
    for i, name in enumerate(names):
        path = os.path.join(masters_dir, name + ".fits")
        hdr = header_of(path)
        cy, cx = quiet_patch(path)
        # The session id the stats are keyed on is not the file name; match on the sanitised form,
        # which is what RetainedMasterStore.PathFor writes.
        rec = next((r for sid, r in stats.items() if sanitize(sid) == name), None)
        # A profile entry can be null where the fit refused that channel (PsfProfileFit reports a
        # refusal rather than a number), so take the first one that actually fitted.
        fwhm = None
        for prof in (rec.get("MasterProfiles") or []) if rec else []:
            if isinstance(prof, dict) and isinstance(prof.get("Fwhm"), (int, float)):
                fwhm = round(float(prof["Fwhm"]), 3)
                break
        rows.append({
            "slug": web_slug(name)[:110],
            "name": name,
            "object": (hdr.get("OBJECT") or name).strip(),
            "date": str(hdr.get("DATE-OBS", ""))[:10],
            "camera": (hdr.get("INSTRUME") or "unknown camera").strip(),
            "filter": (hdr.get("FILTER") or "None").strip(),
            "exposure": float(hdr.get("EXPTIME", 0) or 0),
            "w": int(hdr["NAXIS1"]),
            "h": int(hdr["NAXIS2"]),
            "cropX": int(cx),
            "cropY": int(cy),
            "strategy": (hdr.get("STRATEGY") or "Float16Staged").strip(),
            "subs": int(hdr.get("STACK_N", 0) or 0),
            "lights": int(rec["SubFile"].__len__()) if rec and rec.get("SubFile") else int(hdr.get("STACK_N", 0) or 0),
            "fwhm": fwhm,
            "train": by_train.get(rec["SessionId"]) if rec else None,
            # Stamped by the bake since the coverage/WCS change: a card can say whether the master
            # came out of the oven solved, rather than the gallery solving it again to find out.
            "solved": "CRPIX1" in hdr,
            "coverage": os.path.exists(rejection_path(path)),
            # 'a' / 'b' = one pier side; 'both' = the combined master of a night that WAS split;
            # null = a night that never flipped. flipGroup ties the three together.
            "flip": sides[name],
            "flipGroup": web_slug(flip_group(name))[:110] if sides[name] else None,
            "id": i,
        })
    json.dump(rows, open(out_path, "w", encoding="utf-8"), separators=(",", ":"))
    solved = sum(1 for r in rows if r["solved"])
    cov = sum(1 for r in rows if r["coverage"])
    split = sum(1 for r in rows if r["flip"] in ("a", "b"))
    both = sum(1 for r in rows if r["flip"] == "both")
    print("%d masters -> %s   solved %d   with a coverage plane %d   "
          "%d pier-side masters over %d flipped nights (+%d combined)"
          % (len(rows), out_path, solved, cov, split, split // 2, both))


if __name__ == "__main__":
    main()
