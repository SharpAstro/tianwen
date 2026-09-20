# openngc-audit

Audits the OpenNGC catalogue's common names and cross-identifiers against SIMBAD **by position**:
every name and identifier a row carries is resolved through SIMBAD's TAP service, and the place
SIMBAD puts it is compared with the row's own RA/Dec. A name that lands somewhere else is on the
wrong row. The report on stdout and in `openngc-audit.md` is the review; the CSVs beside it are the
detail.

```bash
python tools/openngc-audit/openngc_simbad_audit.py                    # ~15 min + ~1 s per miss
python tools/openngc-audit/openngc_simbad_audit.py --limit 30         # a dry run, about a minute
python tools/openngc-audit/openngc_simbad_audit.py --no-corroborate   # SIMBAD only (pre-2026-09-20)
```

Defaults: the OpenNGC checkout at `src/TianWen.Lib/OpenNGC/database_files`, outputs next to the
script (git-ignored). Run it after every OpenNGC refresh (`Copy-OpenNGC.ps1`), and whenever a panel
shows a name that looks wrong: that is how the Flame Nebula on IC 434 was found (2026-09-18).

## Why by position, and why three classes

A name cannot be checked by reading it: "Flame Nebula" on IC 434 reads fine to anyone who does not
happen to know NGC 2024, and SIMBAD, Stellarium and Wikipedia disagreed with OpenNGC about it for
years without anyone noticing. A position is a number. SIMBAD resolves `NAME Flame Nebula` to
05 41 43 -01 54 44, and that is 34 arcminutes from the row it sat on.

The distance alone is not the verdict, though, and the report sorts the misses into three classes
measured on the first full run (14,026 rows, 51,631 names and identifiers, 190 raw misses):

- **MISPLACED**: SIMBAD resolves the value to a *different* object, beyond the row's own major axis
  and beyond 10 arcminutes. A wrong number (IC 864's PGC was 7,643 arcminutes off) or a typo (the
  Cocoon Galaxy on NGC 4990 instead of 4490). 17 on the first run, 14 of them galaxy identifiers.
- **SUSPECT**: a different object, but *inside* the row's extent. IC 434 lives here: 34 arcminutes
  inside a 90-arcminute major axis, which is exactly how a large extent hides a wrong name from
  the distance rule. Galaxy-pair component swaps (NGC 2330/2332) live here too. Needs a look by
  hand, with the SIMBAD object beside it.
- **CENTRE**: the value resolves to the *same* designation and only SIMBAD's centre differs (the
  Hyades, the Rosette, the Witch Head). Not an error, listed so the count is honest.

The known IC 434 case must appear under SUSPECT on every run, or the script is wrong; the report
says so in capitals when it does not.

## What is not an error

- A nickname SIMBAD assigns elsewhere while the row's is also in use: SIMBAD's `NAME Butterfly
  Nebula` is M 2-9 while NGC 6302 carries the same nickname (put there on purpose, OpenNGC #34); its
  `NAME Cave Nebula` is not Caldwell 9's Sh2-155. Both are CENTRE-class judgements, not misses.
- The 6,600 unresolved values, almost all SDSS coordinate designations and survey ids that SIMBAD
  spells with a different precision. They are listed in `openngc-audit-unresolved.csv` and nothing
  more.

## How SIMBAD is asked

Batches of 200 identifiers go up as a `TAP_UPLOAD` table and are joined against `ident` and
`basic`, so the answer comes back beside the literal that was asked. A plain `id IN (...)` cannot
do that: SIMBAD folds catalogue synonyms server-side (`PGC 778` matches a row stored as `LEDA 778`)
and echoes the stored spelling, so the answers could not be mapped back to the question. A common
name is asked as `NAME <name>`; an identifier as given, then in obvious respellings (`SH 2-` to
`Sh 2-`, the catalogue prefix in title case) when it does not resolve. One request per second, a
User-Agent naming the project, and a growing pause on a 429 or a 5xx.

## SIMBAD alone is not enough, and the number that proves it

The first upstream PR this audit produced
([mattiaverga/OpenNGC#53](https://github.com/mattiaverga/OpenNGC/pull/53), 18 findings) came back
with **10 rejected**, nearly all on one sentence from the maintainer: *SIMBAD is wrong, NED and
LEDA agree with each other*. A single source cannot tell a catalogue ERROR from a disagreement
BETWEEN catalogues, and this audit was reporting the second as if it were the first.

So every non-CENTRE miss now gets a second opinion (`corroborate.py`): **NED** for any identifier,
**HyperLeda** as well for a PGC/LEDA number (it is the authority for its own numbering, which is the
argument the maintainer made -- "I assume Leda db knows better its own entries"), and
**Stellarium's curated `names.dat`** for a common name. Findings land in three buckets:

- **CONFIRMED** -- no second source puts the value on the row it currently sits on. File upstream.
- **DISPUTED** -- one does. Check the source catalogue before filing.
- **SIMBAD ONLY** -- every second source is silent. File, saying so.

**It is triage, not a filter, and that distinction is measured rather than assumed.** Re-running
that PR's 20 disputed identifier rows through NED: a rule of "only file when a second source agrees
with SIMBAD" would have suppressed **10 of the 12 rows upstream rejected -- and 5 of the 8 it
ACCEPTED**. NED puts `ESO 056-007` on IC 2105, `PGC 089595` on IC 3231, `LEDA 1434085` on IC 3018,
`MCG -04-08-032` on NGC 1232A and `MCG +10-25-025` on NGC 6377, every one of them the row the
accepted fix was moving the value OFF. NED and HyperLeda are compilations too and carry the same
stale cross-identifications OpenNGC does. A DISPUTED row is **unsettled**, not wrong.

What actually settled the contested rows upstream was the **source catalogue** -- the maintainer
opened MCG at HEASARC and the IRAS PSC/FSC tables. The report prints that link per row; opening it
is a human step the tool does not pretend to automate.

### Why Stellarium is the right second opinion for a NAME

`names.dat` is the only curated DSO name list that records **per-name provenance**: a trailing
comment of source keys (`WK`, `S&T`, `WP`, `APOD`, `SIMBAD`, ...) with a legend at the top of the
file. It had also already met this exact problem -- the file carries
`# NGC 4990 _("Cocoon Galaxy") # SIMBAD` as a **commented-out** line: Stellarium saw SIMBAD's
placement, rejected it, and left the evidence in place. The audit reads withdrawn lines for that
reason. And on the case that started all this it is unambiguous: `Flame Nebula` sits on NGC 2024
under nine keys, `WK` ("well-known name which does not need special source ... the preferred name")
among them, and SIMBAD is not one of them.

## What to do with a finding

File the **CONFIRMED** ones upstream in [OpenNGC](https://github.com/mattiaverga/OpenNGC), citing
the SIMBAD object (`https://simbad.cds.unistra.fr/simbad/sim-id?Ident=<name>`) AND the second
opinion, as `openngc-audit.md` prints them. Take the DISPUTED ones to the source catalogue first, or
leave them out: a PR of clean findings is worth more than a long one a maintainer has to re-derive.

Until a fix ships, `OpenNgcCorrections` in `TianWen.Lib` carries the correction, each line pinned by
a test that expects the raw upstream row to still be wrong, so the line is deleted the day the
refresh brings the fix in. A line upstream **declines** is permanent and says so -- see the Flame
Nebula pair, which OpenNGC keeps on IC 434 on a taxonomy argument and we override deliberately.
