"""Clean-room reader for the HNSKY/ASTAP .1476 Gaia extract, written from the format description
in unit_star_database.pas's comments (the layout, not the code).

  - 110-byte text header; byte at offset 109 is the record size (ASCII space means 11).
  - 5-byte Gaia record: ra7 ra8 ra9 dec7 dec8.
      RA  hours   = (ra7  + ra8*256  + ra9*65536) * 24 / (2**24 - 1)
      DEC degrees = (dec7 + dec8*256 + dec9*65536) * 90 / (128*256*256 - 1)
  - FF FF FF is a SECTION HEADER, not a delimiter: it carries the two fields the records omit,
    dec7 = dec9 + 128 and dec8 = mag*10 + 16. Every record after it inherits both. That is the
    whole compression scheme and the reason a star fits in five bytes.
"""
import numpy as np, glob, os

RA_SCALE = 24.0 / (2**24 - 1)
DEC_SCALE = 90.0 / (128 * 256 * 256 - 1)


def read_file(path, dec_lo=-90.0, dec_hi=90.0, mag_max=99.0):
    with open(path, 'rb') as f:
        head = f.read(110)
        rec = 11 if head[109:110] == b' ' else head[109]
        if rec != 5:
            raise NotImplementedError(f'{os.path.basename(path)} has {rec}-byte records; this reads 5')
        buf = np.frombuffer(f.read(), dtype=np.uint8)
    n = len(buf) // 5
    b = buf[:n * 5].reshape(n, 5)
    is_hdr = (b[:, 0] == 255) & (b[:, 1] == 255) & (b[:, 2] == 255)
    # Each record inherits the most recent section header's dec9 and magnitude.
    idx = np.where(is_hdr)[0]
    if len(idx) == 0:
        return np.empty(0), np.empty(0), np.empty(0)
    owner = np.searchsorted(idx, np.arange(n), side='right') - 1
    valid = (~is_hdr) & (owner >= 0)
    dec9 = (b[idx, 3].astype(np.int32) - 128)[owner[valid]]
    mag = ((b[idx, 4].astype(np.float32) - 16.0) / 10.0)[owner[valid]]
    r = b[valid]
    ra = (r[:, 0].astype(np.int64) + r[:, 1].astype(np.int64) * 256 + r[:, 2].astype(np.int64) * 65536) * RA_SCALE
    dec = (r[:, 3].astype(np.int64) + r[:, 4].astype(np.int64) * 256 + dec9.astype(np.int64) * 65536) * DEC_SCALE
    keep = (dec >= dec_lo) & (dec <= dec_hi) & (mag <= mag_max)
    return ra[keep] * 15.0, dec[keep], mag[keep]   # RA in DEGREES


def read_sky(db_dir, pattern, ra_lo, ra_hi, dec_lo, dec_hi, mag_max=99.0):
    """Scan every cell file and keep what lands in the box. The 1476-cell sky partition is not
    implemented: for a handful of fields a full sequential pass is cheaper than getting it wrong."""
    ras, decs, mags = [], [], []
    for p in sorted(glob.glob(os.path.join(db_dir, pattern))):
        a, d, m = read_file(p, dec_lo, dec_hi, mag_max)
        if len(a):
            k = (a >= ra_lo) & (a <= ra_hi)
            if k.any():
                ras.append(a[k]); decs.append(d[k]); mags.append(m[k])
    if not ras:
        return np.empty(0), np.empty(0), np.empty(0)
    return np.concatenate(ras), np.concatenate(decs), np.concatenate(mags)


if __name__ == '__main__':
    # The worked example in the format description: Sirius.
    ra = (0xC3 + 0x06 * 256 + 0x48 * 65536) * RA_SCALE
    dec = (0xD7 + 0x39 * 256 + (0xE8 - 256) * 65536) * DEC_SCALE
    print(f'Sirius RA  decoded {ra:.8f} h   expected 6.75247662   diff {abs(ra-6.75247662):.2e}')
    print(f'Sirius DEC decoded {dec:.7f} deg expected -16.7161401 diff {abs(dec+16.7161401):.2e}')


# STATUS: the DECODE is validated against the format description's own Sirius worked example to
# 2e-9 h / 2e-8 deg. The whole-file traversal is NOT: measured against untruncated Vizier on one
# Orion patch, this reader accounts for only 23 to 40 percent of Gaia stars at BP 13 to 15, where a
# brightest-first density cap should keep essentially all of them. Either D50's selection differs
# from its documentation or the traversal drops records. Use gaia_vizier for anything that must be
# complete; this is the offline path, sound where the image is shallower than the cap (a field whose
# detection dies by BP 15 reads 99.6 percent matched either way).
