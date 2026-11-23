"""Pins the reading of a plate's line of judge_band_rows.py, with both halves, with one, and with a label that ends in
digits. Run with `python -I test_judge_band_parse.py`; exit 0 is a pass."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))        # -I leaves the script's folder off sys.path
from judge_band_parse import parse_row, short

both = parse_row('  plate                                      1.56   45.8 %                  |   0.98  100.0 %  (0.0 % under, 0.0 % over)')
assert both == {'flat': {'median': 1.56, 'under': 45.8},
                'structure': {'median': 0.98, 'within': 100.0, 'under': 0.0, 'over': 0.0}}, both
assert short('Sag', both) == 'Sag judge band flat 1.56 (46% under) structure 0.98 (100% within, 0% over)', short('Sag', both)
no_flat = parse_row('  plate                                       no flat cell                  |   1.00   88.9 %  (11.1 % under, 0.0 % over)')
assert no_flat['flat'] is None and no_flat['structure']['within'] == 88.9, no_flat
assert short('And', no_flat) == 'And judge band flat - structure 1.00 (89% within, 0% over)', short('And', no_flat)
no_structure = parse_row('  plate                                      1.20   90.0 %                  |  no structure cell')
assert no_structure == {'flat': {'median': 1.2, 'under': 90.0}, 'structure': None}, no_structure
digits = parse_row('  w-r4d_synth29_s0                           3.29    0.0 %                  |   1.07   48.4 %  (16.1 % under, 35.5 % over)')
assert digits['flat'] == {'median': 3.29, 'under': 0.0} and digits['structure']['over'] == 35.5, digits
assert parse_row('49 cells read; the judge\'s plate is flat (band under 1.5) on 2 and holds structure (3 or over) on 31') is None
assert parse_row("  plate   flat cells: median band, share under 1.5 | structure cells: band over the judge's, median") is None
assert parse_row('garbage') is None
print('ok')
