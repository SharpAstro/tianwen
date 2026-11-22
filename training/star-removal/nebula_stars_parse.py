"""The one reading of the line nebula_stars_row.py prints, shared by score_whole.py and its test."""
import re


def parse_row(line, label):
    """The six fields of `nebstars <label> bright n N taken T left L % | dark n N taken T left L %`, or None.
    A half with fewer than 5 stars prints `taken - left -` (no % at all), which is a field of '-', never a miss."""
    m = re.match(rf'^nebstars {re.escape(label)} bright n (\d+) taken ([-\d.]+) left ([-\d.]+)(?: %)? ?\| '
                 rf'dark n (\d+) taken ([-\d.]+) left ([-\d.]+)(?: %)?$', line)
    if not m:
        return None
    return {'bright_n': int(m[1]), 'bright_taken': m[2], 'bright_left': m[3],
            'dark_n': int(m[4]), 'dark_taken': m[5], 'dark_left': m[6]}
