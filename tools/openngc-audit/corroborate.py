"""
Second-opinion lookups for the OpenNGC audit: NED, HyperLeda and Stellarium.

WHY THIS EXISTS
---------------
The audit resolved every name and identifier through SIMBAD alone, and the first upstream PR it
produced (mattiaverga/OpenNGC#53, 18 findings) came back with **11 rejected** -- 10 of them on one
sentence: *SIMBAD is wrong, NED and LEDA agree with each other.* A single source cannot tell a
catalogue error from a disagreement between catalogues, and this audit was reporting the second as
if it were the first.

WHAT THIS IS NOT
----------------
**It is not a filter, and the numbers say so.** Re-run over that PR's 20 disputed identifier rows,
a rule of "only file when a second source agrees with SIMBAD" would have suppressed 10 of the 12
rows upstream rejected -- and 5 of the 8 it ACCEPTED (NED puts `ESO 056-007` on IC 2105,
`PGC 089595` on IC 3231, `LEDA 1434085` on IC 3018, `MCG -04-08-032` on NGC 1232A and
`MCG +10-25-025` on NGC 6377, every one of them the row the fix was moving the value OFF). NED and
LEDA are compilations too, and they carry the same kind of stale cross-identification OpenNGC does.

So corroboration sorts findings into three buckets and files the report; a human still decides. The
only thing that actually settled the contested rows was the SOURCE catalogue -- the maintainer went
to MCG at HEASARC and to the IRAS PSC/FSC tables -- which is a human step. What the tool owes is to
put the disagreement and the source-catalogue link in front of that human, not to guess.

THE BUCKETS
-----------
- ``CONFIRMED``   -- SIMBAD says the value is misplaced and no second source puts it on the row it
                    currently sits on. File upstream.
- ``DISPUTED``    -- a second source DOES put it on that row. Do not file without checking the
                    source catalogue; the answer is as likely to be SIMBAD's error as OpenNGC's.
- ``SIMBAD ONLY`` -- every second source is silent. File, saying so.

Common names take a different second opinion: Stellarium's ``names.dat``, which is a curated list
carrying **per-name provenance keys** (``WK``, ``SIMBAD``, ``WP``, ``S&T``, ...). It is the closest
thing to a multi-source consensus that exists in machine-readable form, and it had already met this
exact problem -- the file carries ``# NGC 4990 _("Cocoon Galaxy") # SIMBAD`` as a COMMENTED-OUT
line, i.e. Stellarium saw SIMBAD's placement, rejected it, and left the evidence in place.
"""

import json
import os
import re
import time
import urllib.parse
import urllib.request

USER_AGENT = "TianWen OpenNGC audit (https://github.com/SharpAstro/tianwen)"
PAUSE_SECONDS = 1.0
TIMEOUT_SECONDS = 30

NED_LOOKUP_URL = "https://ned.ipac.caltech.edu/srs/ObjectLookup?name="
LEDA_URL = "http://atlas.obs-hp.fr/hyperleda/fG.cgi?n=a000&c=o&a=csv&z=d&o="
STELLARIUM_NAMES_URL = (
    "https://raw.githubusercontent.com/Stellarium/stellarium/master/nebulae/default/names.dat"
)

# Where a human goes when NED and SIMBAD disagree, by identifier prefix. These are the tables the
# OpenNGC maintainer actually opened to settle mattiaverga/OpenNGC#53, so they are recorded rather
# than guessed at, and the report prints the link beside the row instead of leaving "check the
# source catalogue" as an instruction with no address.
SOURCE_CATALOGUE_LINKS = {
    'MCG': ("MCG (Vorontsov-Velyaminov), via HEASARC",
            "https://heasarc.gsfc.nasa.gov/db-perl/W3Browse/w3table.pl?tablehead=name%3Dmcg"),
    'IRAS': ("IRAS PSC/FSC, via IRSA",
             "https://irsa.ipac.caltech.edu/Missions/iras.html"),
    'PGC': ("HyperLeda, the LEDA/PGC authority",
            "http://atlas.obs-hp.fr/hyperleda/"),
    'LEDA': ("HyperLeda, the LEDA/PGC authority",
             "http://atlas.obs-hp.fr/hyperleda/"),
    'UGC': ("UGC (Nilson), via VizieR VII/26D",
            "https://vizier.cds.unistra.fr/viz-bin/VizieR-3?-source=VII/26D"),
    'ESO': ("ESO/Uppsala Survey, via VizieR VII/34",
            "https://vizier.cds.unistra.fr/viz-bin/VizieR-3?-source=VII/34"),
}

REQUEST_COUNT = 0


def source_catalogue_for(value):
    """The source catalogue a disputed identifier should be settled against, or None."""
    prefix = re.match(r'^([A-Za-z-]+)', value.strip())
    if not prefix:
        return None
    return SOURCE_CATALOGUE_LINKS.get(prefix.group(1).upper().rstrip('-'))


def _get(url, timeout=TIMEOUT_SECONDS):
    global REQUEST_COUNT
    REQUEST_COUNT += 1
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


# ------------------------------------------------------------------------------------- NED --

def ned_preferred_name(value):
    """
    NED's preferred name for an identifier, or None when NED does not know it.

    NED's resolver answers with the object it has cross-identified the designation WITH, which is
    exactly the question at issue -- and note it answers from NED's own compilation, so agreeing
    with OpenNGC is evidence, never proof (see the module doc: it agreed with five rows that were
    wrong).
    """
    try:
        payload = _get(NED_LOOKUP_URL + urllib.parse.quote(value))
    except Exception:
        return None
    try:
        data = json.loads(payload)
    except ValueError:
        return None
    preferred = data.get("Preferred") or {}
    name = preferred.get("Name")
    return name.strip() if name else None


# -------------------------------------------------------------------------------- HyperLeda --

def leda_object_name(value):
    """
    HyperLeda's own name for a PGC/LEDA number, or None.

    Only asked for PGC/LEDA values, and deliberately: HyperLeda IS the authority for its own
    numbering, which is the argument the OpenNGC maintainer made ("I assume Leda db knows better
    its own entries"). Asking it about an MCG or IRAS designation would just be a third compilation
    with no special standing.

    A reply naming the PGC number back at us means LEDA holds no NGC/IC alias for it, which is a
    real answer -- it is how `PGC 163379` and `LEDA 1434085` were shown NOT to be IC 864 and
    IC 3018 -- so it is returned as-is and the caller compares.
    """
    m = re.match(r'^(?:PGC|LEDA)\s*0*(\d+)$', value.strip(), re.IGNORECASE)
    if not m:
        return None
    try:
        payload = _get(LEDA_URL + "PGC" + m.group(1))
    except Exception:
        return None
    text = payload.decode('utf-8', errors='replace')
    for line in text.splitlines():
        if line.startswith('#') or line.startswith('$objname') or not line.strip():
            continue
        return line.split('\t')[0].strip()
    return None


# ------------------------------------------------------------------------------- Stellarium --

# Deliberately NOT the fixed-width layout the file's own header describes ("1-5 prefix, 6-20 id,
# 21-end name"): the data does not honour it -- the live file puts `_("` at index 21, one past where
# that reading lands -- and a column-counted parser silently matches NOTHING, which is what a first
# cut did. Splitting on the `_("` the format is really delimited by costs nothing and cannot drift.
_STELLARIUM_LINE = re.compile(
    r'^(?P<designation>[^_]+?)\s*_\("(?P<name>[^"]+)"\)\s*(?:#\s*(?P<sources>.*))?$'
)


def load_stellarium_names(cache_path=None):
    """
    Stellarium's curated common names: ``{normalised name: [(designation, sources), ...]}``, or
    ``None`` when the file could not be fetched and no cache exists.

    ``None``, not an empty dict, and the caller must keep the two apart: with an empty dict every
    common name would be bucketed SIMBAD ONLY with the detail "names.dat does not carry this name",
    which is a statement about the file made without the file. A download failure is NOT CHECKED.

    Commented-out lines are read too, and they are the most useful rows in the file: a name
    Stellarium DELETED is a placement someone already reviewed and rejected, and the trailing
    comment says on whose authority. ``# NGC 4990 _("Cocoon Galaxy") # SIMBAD`` is the Cocoon Galaxy
    error this audit independently rediscovered, sitting in the file with SIMBAD named as its only
    backer.
    """
    text = None
    if cache_path and os.path.exists(cache_path):
        with open(cache_path, 'r', encoding='utf-8') as f:
            text = f.read()
    if text is None:
        try:
            text = _get(STELLARIUM_NAMES_URL).decode('utf-8', errors='replace')
        except Exception:
            return None
        if cache_path:
            with open(cache_path, 'w', encoding='utf-8') as f:
                f.write(text)

    names = {}
    for raw in text.splitlines():
        line = raw.rstrip()
        withdrawn = False
        if line.startswith('#'):
            stripped = line.lstrip('#').lstrip()
            # Only a commented-out DATA line, not prose: it has to still parse as one.
            if '_("' not in stripped:
                continue
            line = stripped
            withdrawn = True
        # No length test: the comment on _STELLARIUM_LINE explains why a column count is the wrong
        # question here, and a leftover minimum is the same mistake in miniature. The delimiter is
        # the only thing that decides whether this is a data line.
        if '_("' not in line:
            continue
        m = _STELLARIUM_LINE.match(line)
        if not m:
            continue
        designation = re.sub(r'\s+', ' ', m.group('designation').strip())
        entry = {
            'designation': designation,
            'sources': (m.group('sources') or '').strip(),
            'withdrawn': withdrawn,
        }
        names.setdefault(normalise_name(m.group('name')), []).append(entry)
    return names


def source_keys(entry):
    """The provenance keys on one names.dat entry, as a set. Empty when the row cites none."""
    return {k.strip() for k in (entry.get('sources') or '').split(',') if k.strip()}


def normalise_name(s):
    return re.sub(r'\s+', ' ', s).strip().casefold()


def normalise_designation(s):
    """'NGC  4490', 'NGC4490' and 'NGC 4490' are one designation; leading zeros too.

    KNOWN LIMITATION, and it biases toward CONFIRMED. Only NGC and IC are understood, so a
    cross-catalogue alias compares as a DIFFERENT object: if Stellarium carries a name under
    ``PGC 5678`` or ``M 8`` while OpenNGC carries it on the NGC/IC row for the same object, this
    scores the two as disagreeing and the verdict comes out CONFIRMED on what is actually agreement.
    About 40 percent of the live file's entries are non-NGC/IC designations (PGC 154, SH2 43, UGC 40,
    ACO 35, B 32, ESO 27 and so on), so the exposure is real rather than theoretical.

    Closing it needs a cross-identity table, which is the thing the audit is checking rather than
    something it can assume; until then a CONFIRMED whose Stellarium designation is not NGC/IC is
    worth a human glance, and the designation is printed in the detail column for exactly that.
    """
    s = re.sub(r'\s+', '', s or '').upper()
    m = re.match(r'^(NGC|IC)0*(\d+)([A-Z]*)$', s)
    return f"{m.group(1)}{m.group(2)}{m.group(3)}" if m else s


# ---------------------------------------------------------------------------------- ballot --

CONFIRMED = 'CONFIRMED'
DISPUTED = 'DISPUTED'
SIMBAD_ONLY = 'SIMBAD ONLY'


def corroborate_identifier(value, openngc_row, pause=PAUSE_SECONDS):
    """
    Ask NED (and HyperLeda, for a PGC/LEDA number) where an identifier belongs.

    Returns ``(verdict, detail)``. The verdict is DISPUTED as soon as ONE second source puts the
    value back on the row SIMBAD moved it off: one credible disagreement is enough to stop a
    finding being filed as fact, and a unanimity rule would have let through the NGC 2330/2332 and
    NGC 4933A/B swaps that upstream rejected on exactly this evidence.
    """
    row = normalise_designation(openngc_row)
    opinions = []
    agrees_with_row = False

    ned = ned_preferred_name(value)
    if pause:
        time.sleep(pause)
    if ned:
        opinions.append(f"NED: {ned}")
        if normalise_designation(ned) == row:
            agrees_with_row = True

    leda = leda_object_name(value)
    if leda is not None and pause:
        time.sleep(pause)
    if leda:
        opinions.append(f"LEDA: {leda}")
        if normalise_designation(leda) == row:
            agrees_with_row = True

    if not opinions:
        return SIMBAD_ONLY, "NED and LEDA know no such identifier"
    if agrees_with_row:
        return DISPUTED, "; ".join(opinions)
    return CONFIRMED, "; ".join(opinions)


def corroborate_common_name(name, openngc_row, stellarium):
    """
    Ask Stellarium's curated list where a common name belongs.

    A name Stellarium puts on the same row OpenNGC does is DISPUTED for the same reason an
    identifier is. A name it puts elsewhere -- or one it explicitly WITHDREW from this row -- is
    corroboration, and the strongest kind available for a nickname, because the file names its
    sources per entry.
    """
    entries = stellarium.get(normalise_name(name))
    if not entries:
        return SIMBAD_ONLY, "Stellarium's names.dat does not carry this name"

    row = normalise_designation(openngc_row)
    live = [e for e in entries if not e['withdrawn']]
    withdrawn_here = [e for e in entries if e['withdrawn'] and normalise_designation(e['designation']) == row]

    agreeing = [e for e in live if normalise_designation(e['designation']) == row]
    if agreeing:
        # A DISPUTED backed only by OpenNGC's own lineage is not a second opinion. RNGCIC is the
        # Revised NGC/IC, the catalogue OpenNGC derives from, so Stellarium agreeing with the row
        # through it is the row agreeing with itself. 10 of the live file's 1,389 entries were
        # RNGCIC-only on 2026-09-21 (the file moves; the count is a size, not a pin).
        if all(source_keys(e) <= {'RNGCIC'} and source_keys(e) for e in agreeing):
            return SIMBAD_ONLY, (
                "Stellarium agrees with the row, but only on RNGCIC -- OpenNGC's own lineage, so not "
                "an independent opinion")
        return DISPUTED, f"Stellarium also puts it on {openngc_row}"

    # THE independence check, and the one the module's whole argument rests on: a CONFIRMED whose
    # only Stellarium backing is the SIMBAD key is SIMBAD agreeing with SIMBAD, which is the exact
    # circularity this file exists to remove. 155 of the live file's 1,389 entries were SIMBAD-only
    # on 2026-09-21, so this is not a corner. The keys were already printed for a human to catch it;
    # the BUCKET has to catch it too, or the report says "independently confirmed" about a single
    # source.
    if live and all(source_keys(e) == {'SIMBAD'} for e in live):
        return SIMBAD_ONLY, (
            "Stellarium places it elsewhere but cites SIMBAD as its only source, so this is SIMBAD "
            "agreeing with itself rather than a second opinion")

    detail = "; ".join(
        f"Stellarium: {e['designation']}" + (f" [{e['sources']}]" if e['sources'] else "")
        for e in live
    ) or "Stellarium carries the name only as a withdrawn entry"
    if withdrawn_here:
        detail += f" -- and WITHDREW it from {openngc_row}"
        if withdrawn_here[0]['sources']:
            detail += f" (was sourced: {withdrawn_here[0]['sources']})"
    return CONFIRMED, detail
