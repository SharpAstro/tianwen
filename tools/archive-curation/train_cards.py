"""Read-only: what optical-train cards each Organized flat set and light session carries.
One frame per leaf folder (headers only)."""
import os
from astropy.io import fits

ROOT = 'D:/Astro-Organized'
CARDS = ('INSTRUME', 'TELESCOP', 'FOCALLEN', 'APTDIA', 'FOCRATIO')


def first_fits(d):
    for n in sorted(os.listdir(d)):
        if n.lower().endswith(('.fits', '.fit', '.fts')):
            return os.path.join(d, n)
    return None


def leaves(sub):
    base = os.path.join(ROOT, sub)
    for dirpath, dirnames, filenames in os.walk(base):
        if any(f.lower().endswith(('.fits', '.fit', '.fts')) for f in filenames):
            yield dirpath


for sub in ('flats', 'lights'):
    rows = []
    for d in leaves(sub):
        f = first_fits(d)
        h = fits.getheader(f)
        vals = [str(h.get(c, '')).strip() for c in CARDS]
        rows.append((os.path.relpath(d, os.path.join(ROOT, sub)).replace('\\', '/'), vals))
    print(f'== {sub}: {len(rows)} folders')
    for rel, vals in rows:
        print(f'{rel:70s} ' + ' | '.join(f'{c}={v}' for c, v in zip(CARDS, vals) if c != 'INSTRUME' or True))
    no_train = [r for r, v in rows if not v[1] and not v[2]]
    print(f'-- {sub}: {len(no_train)} of {len(rows)} carry neither TELESCOP nor FOCALLEN')
