"""Pins the reading of nebula_stars_row.py's line, in both of its wordings. Run with `python -I test_nebula_stars_parse.py`; exit 0 is a pass."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))        # -I leaves the script's folder off sys.path
from nebula_stars_parse import parse_row

full = parse_row('nebstars Sag bright n 40 taken 0.31 left 12.5 % | dark n 30 taken 0.95 left 0.0 %', 'Sag')
assert full == {'bright_n': 40, 'bright_taken': '0.31', 'bright_left': '12.5', 'dark_n': 30, 'dark_taken': '0.95', 'dark_left': '0.0'}, full
few = parse_row('nebstars Sag bright n 4 taken - left - | dark n 0 taken - left -', 'Sag')
assert few == {'bright_n': 4, 'bright_taken': '-', 'bright_left': '-', 'dark_n': 0, 'dark_taken': '-', 'dark_left': '-'}, few
mixed = parse_row('nebstars Tar bright n 9 taken 0.4 left 3.0 % | dark n 2 taken - left -', 'Tar')
assert mixed and mixed['dark_left'] == '-' and mixed['bright_left'] == '3.0', mixed
assert parse_row('nebstars Tar bright n 9 taken 0.4 left 3.0 % | dark n 2 taken - left -', 'Sag') is None
assert parse_row('garbage', 'Sag') is None
print('ok')
