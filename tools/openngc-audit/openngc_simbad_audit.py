#!/usr/bin/env python
"""
Audit OpenNGC's common names and cross-identifiers against SIMBAD by position.

For every OpenNGC row (NGC.csv + addendum.csv) with a valid RA/Dec, resolve each
common name and cross-identifier the row carries via SIMBAD's TAP service and
compare SIMBAD's position for that name/identifier against the row's own
declared position. Anything more than 5 arcminutes away, or more than the row's
own MajAx away when MajAx is smaller than 5, is a "miss".

The report then sorts the misses into what they usually are (measured on the
2026-09-18 run, 14,026 rows, 190 raw misses):
  - MISPLACED: SIMBAD resolves the name to a DIFFERENT object, beyond the row's
    own extent and beyond 10 arcminutes. A wrong row (IC 864's PGC number was
    7,643 arcminutes off) or a typo (the Cocoon Galaxy on NGC 4990 for 4490).
  - SUSPECT: a different object, but inside the row's extent. The IC 434 case
    lives here: the Flame Nebula 34 arcminutes north inside a 90-arcminute
    major axis. Also galaxy-pair component swaps. Needs a look by hand.
  - CENTRE: SIMBAD resolves the name to the SAME designation and only its
    centre differs (the Hyades, the Rosette, the Witch Head). Not an error.

The report is the review; re-run it after every OpenNGC refresh. The known
IC 434 case must appear under SUSPECT, or the script is wrong.

Usage (from the repository root):
  python tools/openngc-audit/openngc_simbad_audit.py [--openngc <dir>] [--out <dir>] [--limit N]
Defaults: the OpenNGC checkout at src/TianWen.Lib/OpenNGC/database_files, the
outputs next to this script (git-ignored). About 15 minutes and 300 requests.
"""
import argparse
import csv
import json
import math
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.sax.saxutils as saxutils
import re

import corroborate

HERE = os.path.dirname(os.path.abspath(__file__))
OPENNGC_DIR = os.path.normpath(os.path.join(HERE, '..', '..', 'src', 'TianWen.Lib', 'OpenNGC', 'database_files'))
OUT_DIR = HERE
ROW_LIMIT = 0
CORROBORATE = True

TAP_URL = "https://simbad.cds.unistra.fr/simbad/sim-tap/sync"
USER_AGENT = "TianWen OpenNGC audit (https://github.com/SharpAstro/tianwen)"
BATCH_SIZE = 200
PAUSE_SECONDS = 1.0
MAX_RETRIES = 6
MISS_THRESHOLD_ARCMIN = 5.0

REQUEST_COUNT = 0


def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# ---------------------------------------------------------------- geometry --

def parse_ra_deg(s):
    s = s.strip()
    if not s:
        return None
    h, m, sec = s.split(':')
    return (abs(float(h)) + float(m) / 60.0 + float(sec) / 3600.0) * 15.0


def parse_dec_deg(s):
    s = s.strip()
    if not s:
        return None
    sign = -1.0 if s.startswith('-') else 1.0
    s2 = s.lstrip('+-')
    d, m, sec = s2.split(':')
    return sign * (float(d) + float(m) / 60.0 + float(sec) / 3600.0)


def great_circle_sep_arcmin(ra1, dec1, ra2, dec2):
    ra1r, dec1r, ra2r, dec2r = map(math.radians, (ra1, dec1, ra2, dec2))
    dra = ra2r - ra1r
    ddec = dec2r - dec1r
    a = math.sin(ddec / 2.0) ** 2 + math.cos(dec1r) * math.cos(dec2r) * math.sin(dra / 2.0) ** 2
    a = min(1.0, max(0.0, a))
    c = 2.0 * math.asin(math.sqrt(a))
    return math.degrees(c) * 60.0


# -------------------------------------------------------------------- data --

def load_rows():
    rows = []
    skipped_no_pos = 0
    limit = ROW_LIMIT
    for fn in ('NGC.csv', 'addendum.csv'):
        path = os.path.join(OPENNGC_DIR, fn)
        with open(path, encoding='utf-8') as f:
            reader = csv.DictReader(f, delimiter=';')
            for row in reader:
                name = (row.get('Name') or '').strip()
                ra_s = (row.get('RA') or '').strip()
                dec_s = (row.get('Dec') or '').strip()
                if not name or not ra_s or not dec_s:
                    skipped_no_pos += 1
                    continue
                try:
                    ra = parse_ra_deg(ra_s)
                    dec = parse_dec_deg(dec_s)
                except Exception:
                    skipped_no_pos += 1
                    continue
                majax_s = (row.get('MajAx') or '').strip()
                try:
                    majax = float(majax_s) if majax_s else None
                except ValueError:
                    majax = None
                idents = [x.strip() for x in (row.get('Identifiers') or '').split(',') if x.strip()]
                cnames = [x.strip() for x in (row.get('Common names') or '').split(',') if x.strip()]
                rows.append({
                    'name': name, 'ra': ra, 'dec': dec, 'majax': majax,
                    'identifiers': idents, 'common_names': cnames, 'source': fn,
                })
    if limit > 0:
        must_keep = [r for r in rows if r['name'] == 'IC0434']
        rest = [r for r in rows if r['name'] != 'IC0434']
        rows = must_keep + rest[:limit]
    return rows, skipped_no_pos


# ------------------------------------------------------------------ SIMBAD --
#
# We resolve identifiers via TAP_UPLOAD rather than a plain `i.id IN (...)`
# query. SIMBAD's identifier matching folds catalogue synonyms server-side
# (e.g. a query for 'PGC 778' matches the ident row stored as 'LEDA   778',
# since PGC was absorbed into LEDA) and echoes back whatever it has stored,
# not what was asked for -- so a plain IN-list query returns rows we cannot
# reliably map back to the value we queried. Uploading our batch as a table
# and joining against it means the query value we get back (`qval`) is our
# own literal string, verbatim, regardless of any server-side alias folding.
# ADQL here supports neither UNION nor CASE, which ruled out both of those
# as workarounds.

def build_votable(colname, values):
    rows = ''.join(f'<TR><TD>{saxutils.escape(v)}</TD></TR>' for v in values)
    xml = (
        '<?xml version="1.0"?>\n'
        '<VOTABLE version="1.3" xmlns="http://www.ivoa.net/xml/VOTable/v1.3">\n'
        '<RESOURCE><TABLE>\n'
        f'<FIELD name="{colname}" datatype="char" arraysize="*"/>\n'
        f'<DATA><TABLEDATA>{rows}</TABLEDATA></DATA>\n'
        '</TABLE></RESOURCE>\n'
        '</VOTABLE>\n'
    )
    return xml.encode('utf-8')


def build_multipart(fields, file_field, file_bytes, filename, content_type):
    boundary = 'AuditBoundary' + uuid.uuid4().hex
    body = bytearray()
    for name, value in fields.items():
        body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n'.encode('utf-8')
        body += value.encode('utf-8')
        body += b'\r\n'
    body += (
        f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; '
        f'filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'
    ).encode('utf-8')
    body += file_bytes
    body += b'\r\n'
    body += f'--{boundary}--\r\n'.encode('utf-8')
    return bytes(body), boundary


def run_upload_query(query_string, table_name, votable_bytes):
    global REQUEST_COUNT
    fields = {
        'REQUEST': 'doQuery', 'LANG': 'ADQL', 'FORMAT': 'json',
        'UPLOAD': f'{table_name},param:{table_name}', 'QUERY': query_string,
    }
    body, boundary = build_multipart(fields, table_name, votable_bytes, f'{table_name}.xml', 'text/xml')
    delay = 2.0
    last_err = None
    for attempt in range(MAX_RETRIES):
        req = urllib.request.Request(TAP_URL, data=body, headers={
            'User-Agent': USER_AGENT,
            'Content-Type': f'multipart/form-data; boundary={boundary}',
        })
        REQUEST_COUNT += 1
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                return json.loads(resp.read().decode('utf-8'))
        except urllib.error.HTTPError as e:
            last_err = e
            if e.code == 429 or 500 <= e.code < 600:
                log(f"    HTTP {e.code}, retry {attempt + 1}/{MAX_RETRIES} after {delay:.0f}s")
                time.sleep(delay)
                delay *= 2
                continue
            raise
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            last_err = e
            log(f"    network error {e!r}, retry {attempt + 1}/{MAX_RETRIES} after {delay:.0f}s")
            time.sleep(delay)
            delay *= 2
            continue
    raise RuntimeError(f"query failed after {MAX_RETRIES} retries: {last_err!r}")


def resolve_values(values, resolved, failed_batches):
    """values: iterable of unique SIMBAD-spelling strings to resolve.
    Updates `resolved` dict: raw_value -> list of (main_id, otype, ra_deg, dec_deg).
    Only queries values not already present in `resolved`."""
    todo = sorted(v for v in dict.fromkeys(values) if v not in resolved)
    if not todo:
        return
    n_batches = (len(todo) + BATCH_SIZE - 1) // BATCH_SIZE
    query = (
        "SELECT u.qval, b.main_id, b.otype, b.ra, b.dec "
        "FROM TAP_UPLOAD.batch AS u "
        "JOIN ident AS i ON i.id = u.qval "
        "JOIN basic AS b ON b.oid = i.oidref"
    )
    for bi in range(n_batches):
        chunk = todo[bi * BATCH_SIZE:(bi + 1) * BATCH_SIZE]
        log(f"  batch {bi + 1}/{n_batches} ({len(chunk)} ids, request #{REQUEST_COUNT + 1})")
        votable = build_votable('qval', chunk)
        try:
            resp = run_upload_query(query, 'batch', votable)
        except Exception as e:
            log(f"  BATCH FAILED, recording as failed: {e!r}")
            failed_batches.append({'chunk': chunk, 'error': repr(e)})
            time.sleep(PAUSE_SECONDS)
            continue
        cols = [m['name'] for m in resp.get('metadata', [])]
        idx = {c: n for n, c in enumerate(cols)}
        for row in resp.get('data', []):
            qval = row[idx['qval']]
            main_id = row[idx['main_id']]
            otype = row[idx['otype']]
            ra = row[idx['ra']]
            dec = row[idx['dec']]
            resolved.setdefault(qval, []).append((main_id, otype, ra, dec))
        time.sleep(PAUSE_SECONDS)


def respelling_candidates(value):
    """Obvious re-spellings to try when the identifier as given doesn't resolve."""
    out = []
    v = value.strip()
    if v.upper().startswith('SH 2-') or v.upper().startswith('SH2-'):
        rest = v.split('2-', 1)[-1] if '2-' in v else v
        out.append('Sh 2-' + rest)
    parts = v.split(' ', 1)
    if len(parts) == 2:
        prefix, rest = parts
        if prefix.isalpha() and prefix.isupper() and len(prefix) > 1:
            out.append(prefix.capitalize() + ' ' + rest)
            out.append(prefix.title() + ' ' + rest)
    # de-duplicate while preserving order, drop the original spelling if repeated
    seen = set()
    uniq = []
    for c in out:
        if c != value and c not in seen:
            uniq.append(c)
            seen.add(c)
    return uniq


# ---------------------------------------------------------------- classify --

def normalise_designation(s):
    """'NGC  4490' and 'NGC4490' and 'NGC 4490' are one designation; leading zeros too."""
    s = re.sub(r'\s+', '', s).upper()
    m = re.match(r'^(NGC|IC)0*(\d+)([A-Z]*)$', s)
    return f"{m.group(1)}{m.group(2)}{m.group(3)}" if m else s


def classify(miss):
    """MISPLACED / SUSPECT / CENTRE, see the module doc."""
    sep = miss['separation_arcmin']
    majax = miss['majax_arcmin'] if isinstance(miss['majax_arcmin'], float) else 0.0
    same = normalise_designation(miss['simbad_main_id']) == normalise_designation(miss['openngc_name'])
    if same:
        return 'CENTRE'
    return 'MISPLACED' if sep > max(10.0, majax) else 'SUSPECT'


# -------------------------------------------------------------------- main --

def parse_args():
    global OPENNGC_DIR, OUT_DIR, ROW_LIMIT, CORROBORATE
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--openngc', default=OPENNGC_DIR, help='OpenNGC database_files directory')
    p.add_argument('--out', default=OUT_DIR, help='where the three output files go')
    p.add_argument('--limit', type=int, default=0, help='audit only the first N rows (plus IC0434), for a dry run')
    p.add_argument('--no-corroborate', action='store_true',
                   help='skip the NED / HyperLeda / Stellarium second opinion (SIMBAD only, the pre-2026-09-20 behaviour)')
    a = p.parse_args()
    OPENNGC_DIR = a.openngc
    OUT_DIR = a.out
    ROW_LIMIT = a.limit
    CORROBORATE = not a.no_corroborate
    os.makedirs(OUT_DIR, exist_ok=True)


def main():
    parse_args()
    t0 = time.time()
    log("Loading OpenNGC rows...")
    rows, skipped_no_pos = load_rows()
    log(f"Loaded {len(rows)} rows with RA/Dec ({skipped_no_pos} rows skipped for missing RA/Dec)")

    items = []  # each: dict(openngc_name, kind, value, query_string, row_ref_index)
    for ridx, row in enumerate(rows):
        for cname in row['common_names']:
            items.append({'row': ridx, 'kind': 'common name', 'value': cname,
                           'query_string': f'NAME {cname}'})
        for ident in row['identifiers']:
            items.append({'row': ridx, 'kind': 'identifier', 'value': ident,
                           'query_string': ident})

    n_common = sum(1 for i in items if i['kind'] == 'common name')
    n_ident = sum(1 for i in items if i['kind'] == 'identifier')
    log(f"{len(items)} name/identifier checks to make ({n_common} common names, {n_ident} identifiers)")

    resolved = {}  # query_string -> list of (main_id, otype, ra, dec)
    failed_batches = []

    log("Pass 1: resolving as given...")
    resolve_values((i['query_string'] for i in items), resolved, failed_batches)

    unresolved_after_pass1 = [i for i in items if i['query_string'] not in resolved]
    log(f"Pass 1 done. {len(unresolved_after_pass1)} still unresolved; trying respellings...")

    respell_map = {}  # item index in unresolved_after_pass1 -> list of candidate strings
    candidate_pool = []
    for i in unresolved_after_pass1:
        if i['kind'] != 'identifier':
            continue
        cands = respelling_candidates(i['value'])
        if cands:
            respell_map[id(i)] = cands
            candidate_pool.extend(cands)

    if candidate_pool:
        log(f"Pass 2: {len(set(candidate_pool))} unique respelling candidates...")
        resolve_values(candidate_pool, resolved, failed_batches)
    else:
        log("Pass 2: nothing to try.")

    log(f"Total SIMBAD requests made: {REQUEST_COUNT}")
    if failed_batches:
        log(f"WARNING: {len(failed_batches)} batches failed after retries and were skipped.")

    # ---------------------------------------------------------------- score --
    misses = []
    unresolved = []
    n_resolved_final = 0

    for i in items:
        row = rows[i['row']]
        matches = resolved.get(i['query_string'])
        used_value = i['query_string']
        if not matches:
            cands = respell_map.get(id(i), []) if i['kind'] == 'identifier' else []
            # find via unresolved_after_pass1 identity — but `i` here is the same object
            # only when kind == identifier and it was in unresolved_after_pass1; recompute directly:
            if i['kind'] == 'identifier':
                for c in respelling_candidates(i['value']):
                    if c in resolved:
                        matches = resolved[c]
                        used_value = c
                        break
        if not matches:
            unresolved.append({'openngc_name': row['name'], 'kind': i['kind'], 'value': i['value']})
            continue

        n_resolved_final += 1
        # pick the closest match (benefit of the doubt for ambiguous identifiers)
        best = None
        best_sep = None
        for main_id, otype, sra, sdec in matches:
            if sra is None or sdec is None:
                continue
            sep = great_circle_sep_arcmin(row['ra'], row['dec'], sra, sdec)
            if best_sep is None or sep < best_sep:
                best_sep = sep
                best = (main_id, otype, sra, sdec)
        if best is None:
            unresolved.append({'openngc_name': row['name'], 'kind': i['kind'], 'value': i['value']})
            n_resolved_final -= 1
            continue

        main_id, otype, sra, sdec = best
        majax = row['majax']
        # Miss rule: separation > 5 arcmin OR separation > MajAx (two independent
        # absolute tests, OR'd) -- algebraically separation > min(5, MajAx). MajAx
        # only ever *tightens* the threshold (for small/compact objects); a large
        # MajAx (e.g. IC0434's 90') must not exempt an otherwise-real miss.
        threshold = MISS_THRESHOLD_ARCMIN
        if majax is not None and majax < MISS_THRESHOLD_ARCMIN:
            threshold = majax
        if best_sep > threshold:
            misses.append({
                'openngc_name': row['name'],
                'kind': i['kind'],
                'value': i['value'],
                'simbad_main_id': main_id,
                'simbad_otype': otype,
                'openngc_ra_deg': row['ra'],
                'openngc_dec_deg': row['dec'],
                'simbad_ra_deg': sra,
                'simbad_dec_deg': sdec,
                'separation_arcmin': best_sep,
                'majax_arcmin': majax if majax is not None else '',
            })

    for m in misses:
        m['class'] = classify(m)
    misses.sort(key=lambda m: ({'MISPLACED': 0, 'SUSPECT': 1, 'CENTRE': 2}[m['class']], -m['separation_arcmin']))

    # ---------------------------------------------------------- second opinion --
    #
    # SIMBAD alone cannot tell a catalogue ERROR from a disagreement BETWEEN catalogues, and the
    # first PR this audit produced (mattiaverga/OpenNGC#53) had 11 of 18 findings rejected -- 10 of
    # them on exactly that: "SIMBAD is wrong, NED and LEDA agree with each other". So every miss gets
    # asked of a second source -- and the result is a BUCKET, never a filter. See corroborate.py
    # for why filtering was measured and rejected: it would have suppressed 10 of the 12 rejected
    # rows and 5 of the 8 ACCEPTED ones.
    #
    # CENTRE-class misses are skipped: they are the same designation with a different centre, which
    # is not a claim about which object a value belongs to, so there is nothing to corroborate.
    if CORROBORATE:
        to_check = [m for m in misses if m['class'] != 'CENTRE']
        log(f"Corroborating {len(to_check)} non-CENTRE misses against NED / HyperLeda / Stellarium...")
        stellarium = corroborate.load_stellarium_names(os.path.join(OUT_DIR, 'stellarium-names.dat'))
        if stellarium is None:
            log("  Stellarium names.dat: UNAVAILABLE (download failed, no cache); common names stay NOT CHECKED")
        else:
            log(f"  Stellarium names.dat: {len(stellarium)} names")
        for n, m in enumerate(to_check, 1):
            if m['kind'] == 'common name':
                if stellarium is None:
                    # Not a verdict: the second source could not be consulted, and saying SIMBAD ONLY
                    # here would report a network failure as "nobody else knows the name".
                    verdict, detail = 'NOT CHECKED', "Stellarium's names.dat could not be fetched"
                else:
                    verdict, detail = corroborate.corroborate_common_name(
                        m['value'], m['openngc_name'], stellarium)
            else:
                verdict, detail = corroborate.corroborate_identifier(m['value'], m['openngc_name'])
            m['corroboration'] = verdict
            m['corroboration_detail'] = detail
            src = corroborate.source_catalogue_for(m['value']) if m['kind'] == 'identifier' else None
            m['source_catalogue'] = f"{src[0]} -- {src[1]}" if src else ''
            if n % 25 == 0:
                log(f"  {n}/{len(to_check)}")
        log(f"Second-opinion requests made: {corroborate.REQUEST_COUNT}")
    for m in misses:
        # 'NOT CHECKED' either way: a CENTRE miss is skipped deliberately (it is the same
        # designation with a different centre, so there is no "which object" to corroborate), and a
        # --no-corroborate run skipped everything. The distinction is in the summary, not the row.
        m.setdefault('corroboration', 'NOT CHECKED')
        m.setdefault('corroboration_detail', '')
        m.setdefault('source_catalogue', '')

    # ------------------------------------------------------------- outputs --
    misses_path = os.path.join(OUT_DIR, 'openngc-audit-misses.csv')
    with open(misses_path, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=[
            'class', 'corroboration', 'openngc_name', 'kind', 'value',
            'simbad_main_id', 'simbad_otype',
            'openngc_ra_deg', 'openngc_dec_deg', 'simbad_ra_deg', 'simbad_dec_deg',
            'separation_arcmin', 'majax_arcmin',
            'corroboration_detail', 'source_catalogue',
        ])
        w.writeheader()
        for m in misses:
            w.writerow(m)

    unresolved_path = os.path.join(OUT_DIR, 'openngc-audit-unresolved.csv')
    with open(unresolved_path, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=['openngc_name', 'kind', 'value'])
        w.writeheader()
        for u in unresolved:
            w.writerow(u)

    elapsed = time.time() - t0

    # bands
    band_5_10 = [m for m in misses if 5.0 < m['separation_arcmin'] <= 10.0]
    band_10_30 = [m for m in misses if 10.0 < m['separation_arcmin'] <= 30.0]
    band_30p = [m for m in misses if m['separation_arcmin'] > 30.0]
    miss_common = [m for m in misses if m['kind'] == 'common name']
    miss_ident = [m for m in misses if m['kind'] == 'identifier']

    ic0434_found = any(
        m['openngc_name'] == 'IC0434' and m['value'] in ('Flame Nebula', 'LBN 953') and m['class'] == 'SUSPECT'
        for m in misses
    )
    by_class = {c: [m for m in misses if m['class'] == c] for c in ('MISPLACED', 'SUSPECT', 'CENTRE')}

    md_path = os.path.join(OUT_DIR, 'openngc-audit.md')
    with open(md_path, 'w', encoding='utf-8') as f:
        f.write("# OpenNGC vs SIMBAD position audit\n\n")
        f.write(
            "Audits every common name and cross-identifier OpenNGC attaches to a row, "
            "by asking SIMBAD (TAP) where that name/identifier actually points, and "
            "comparing against the row's own declared RA/Dec.\n\n"
        )
        f.write("## Summary\n\n")
        f.write(f"- Rows checked: {len(rows)} ({skipped_no_pos} rows skipped, no RA/Dec)\n")
        f.write(f"- Names/identifiers checked: {len(items)} ({n_common} common names, {n_ident} identifiers)\n")
        f.write(f"- Resolved by SIMBAD: {n_resolved_final}\n")
        f.write(f"- Unresolved (SIMBAD has no such name/identifier): {len(unresolved)}\n")
        f.write(
            f"- Misplaced (separation > 5 arcmin, or > MajAx arcmin when MajAx is "
            f"smaller than 5): {len(misses)}\n"
        )
        f.write(f"  - by kind: {len(miss_common)} common names, {len(miss_ident)} identifiers\n")
        f.write(f"  - by class: {len(by_class['MISPLACED'])} MISPLACED (a different object beyond the row's extent and 10'), "
                f"{len(by_class['SUSPECT'])} SUSPECT (a different object within the extent), "
                f"{len(by_class['CENTRE'])} CENTRE (the same designation, only the centre differs; not an error)\n")
        f.write(f"  - by distance band: {len(band_5_10)} in 5-10', {len(band_10_30)} in 10-30', {len(band_30p)} over 30'\n")
        f.write(f"- SIMBAD requests made: {REQUEST_COUNT}\n")
        f.write(f"- Failed batches (skipped after retries): {len(failed_batches)}\n")
        f.write(f"- Run time: {elapsed:.1f} s ({elapsed / 60.0:.1f} min)\n\n")

        by_corr = {v: [m for m in misses if m.get('corroboration') == v]
                   for v in ('CONFIRMED', 'DISPUTED', 'SIMBAD ONLY', 'NOT CHECKED')}
        f.write("## Second opinion: what to actually file\n\n")
        f.write(
            "Every non-CENTRE miss is put to a second source -- NED for any identifier, "
            "HyperLeda as well for a PGC/LEDA number (it is the authority for its own "
            "numbering), Stellarium's curated `names.dat` for a common name.\n\n"
            "**This is triage, not a filter, and the reason is measured.** The first PR this "
            "audit produced ([mattiaverga/OpenNGC#53]"
            "(https://github.com/mattiaverga/OpenNGC/pull/53)) had **11 of 18 findings "
            "rejected**, 10 of them on one sentence from the maintainer: *SIMBAD is wrong, NED "
            "and LEDA agree with each other*. But re-running those 20 identifier rows through "
            "NED shows that a rule of 'only file when a second source agrees with SIMBAD' "
            "would have suppressed **10 of the 12 rejected rows AND 5 of the 8 accepted "
            "ones** -- NED puts `ESO 056-007` on IC 2105, `PGC 089595` on IC 3231, "
            "`LEDA 1434085` on IC 3018, `MCG -04-08-032` on NGC 1232A and `MCG +10-25-025` on "
            "NGC 6377, every one of them the row the accepted fix was moving the value OFF. "
            "NED and HyperLeda are compilations too, and they carry the same stale "
            "cross-identifications OpenNGC does.\n\n"
            "So a DISPUTED row is not wrong, it is **unsettled** -- and the only thing that "
            "settled them upstream was the SOURCE catalogue (MCG at HEASARC, the IRAS PSC/FSC "
            "tables). That link is printed per row; opening it is the human step this tool "
            "does not pretend to automate.\n\n"
        )
        f.write(f"- **CONFIRMED** ({len(by_corr['CONFIRMED'])}): no second source puts the "
                f"value on the row it sits on. File upstream.\n")
        f.write(f"- **DISPUTED** ({len(by_corr['DISPUTED'])}): a second source does. Check the "
                f"source catalogue BEFORE filing.\n")
        f.write(f"- **SIMBAD ONLY** ({len(by_corr['SIMBAD ONLY'])}): every second source is "
                f"silent. File, saying so.\n")
        f.write(f"- NOT CHECKED ({len(by_corr['NOT CHECKED'])}): CENTRE-class, the run used "
                f"`--no-corroborate`, or a common name whose names.dat could not be fetched.\n\n")

        for verdict, blurb in (
            ('CONFIRMED', 'no second source backs the row: this is the list to send upstream'),
            ('DISPUTED', 'NED, HyperLeda or Stellarium puts the value where OpenNGC already has '
                         'it -- settle it against the source catalogue before filing'),
            ('SIMBAD ONLY', 'nobody else knows the value; SIMBAD is the only opinion available'),
        ):
            bucket = by_corr[verdict]
            if not bucket:
                continue
            f.write(f"### {verdict} ({len(bucket)}) -- {blurb}\n\n")
            f.write("| Row | Kind | Value | SIMBAD says | Sep | Second opinion | Source catalogue |\n")
            f.write("|---|---|---|---|---|---|---|\n")
            for m in bucket:
                f.write(
                    f"| {m['openngc_name']} | {m['kind']} | `{m['value']}` "
                    f"| {m['simbad_main_id']} | {m['separation_arcmin']:.1f}' "
                    f"| {m.get('corroboration_detail', '')} "
                    f"| {m.get('source_catalogue', '')} |\n"
                )
            f.write("\n")

        f.write("## IC0434 / Flame Nebula check\n\n")
        if ic0434_found:
            f.write(
                "**FOUND, under SUSPECT.** IC0434's `LBN 953` and/or `Flame Nebula` entries are in the "
                "list below as expected (SIMBAD resolves both to NGC 2024's position, ~34 arcmin north of "
                "the IC0434 row, inside its 90' major axis, which is why the class is SUSPECT and not "
                "MISPLACED: a large extent hides a wrong name from the distance rule alone).\n\n"
            )
        else:
            f.write(
                "**NOT FOUND — this means the script is wrong.** The known IC0434 / "
                "Flame Nebula / LBN 953 defect did not show up in the misplaced list. "
                "Treat the rest of this report with suspicion until that is fixed.\n\n"
            )
            for i in items:
                if rows[i['row']]['name'] == 'IC0434':
                    m = resolved.get(i['query_string'])
                    f.write(f"- debug: IC0434 `{i['kind']}` = `{i['value']}` -> resolved matches: {m}\n")

        for cls, blurb in (
            ('MISPLACED', 'a different object, beyond the row\'s own extent and beyond 10 arcminutes: fix upstream'),
            ('SUSPECT', 'a different object inside the row\'s extent: look by hand (IC 434 lives here, so do galaxy-pair component swaps)'),
            ('CENTRE', 'the same designation, only SIMBAD\'s centre differs: not an error'),
        ):
            f.write(f"\n## {cls} ({len(by_class[cls])}): {blurb}\n\n")
            if by_class[cls]:
                f.write("| OpenNGC row | kind | value | SIMBAD main_id | SIMBAD otype | separation (arcmin) | MajAx (arcmin) |\n")
                f.write("|---|---|---|---|---|---:|---:|\n")
                for m in by_class[cls]:
                    majax_disp = f"{m['majax_arcmin']:.2f}" if isinstance(m['majax_arcmin'], float) else ''
                    f.write(
                        f"| {m['openngc_name']} | {m['kind']} | {m['value']} | {m['simbad_main_id']} | "
                        f"{m['simbad_otype']} | {m['separation_arcmin']:.2f} | {majax_disp} |\n"
                    )
            else:
                f.write("(none)\n")

        f.write("\n## Output files\n\n")
        f.write(f"- `{os.path.basename(misses_path)}` — full misplaced-name detail\n")
        f.write(f"- `{os.path.basename(unresolved_path)}` — names/identifiers SIMBAD does not know\n")

    log(f"Wrote {misses_path}")
    log(f"Wrote {unresolved_path}")
    log(f"Wrote {md_path}")
    log(f"DONE in {elapsed:.1f}s. misses={len(misses)} unresolved={len(unresolved)} requests={REQUEST_COUNT} ic0434_found={ic0434_found}")


if __name__ == '__main__':
    main()
