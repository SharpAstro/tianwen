"""The one reading of a plate's line as judge_band_rows.py prints it, shared by score_whole.py and its test."""
import re


def parse_row(line):
    """The two halves of `  <label>  <median band>  <share under> %  |  <median over the judge's>  <share within> %
    (<under> % under, <over> % over)`, or None when the line is not one. A half with no cell prints `no flat cell` or
    `no structure cell`, which is a half of None, never a miss: a frame the judge leaves flat nowhere has no flat half."""
    if '|' not in line:
        return None
    left, right = line.split('|', 1)
    flat = re.search(r'([\d.]+)\s+([\d.]+) %\s*$', left)
    structure = re.match(r'^\s*([\d.]+)\s+([\d.]+) %\s+\(([\d.]+) % under, ([\d.]+) % over\)\s*$', right)
    if not flat and not left.rstrip().endswith('no flat cell'):
        return None
    if not structure and right.strip() != 'no structure cell':
        return None
    return {'flat': {'median': float(flat[1]), 'under': float(flat[2])} if flat else None,
            'structure': {'median': float(structure[1]), 'within': float(structure[2]), 'under': float(structure[3]),
                          'over': float(structure[4])} if structure else None}


def short(label, row):
    """The row's field: the mottle on the cells the judge leaves flat, the detail on the cells where it keeps structure."""
    f, s = row['flat'], row['structure']
    flat = f"flat {f['median']:.2f} ({f['under']:.0f}% under)" if f else 'flat -'
    structure = f"structure {s['median']:.2f} ({s['within']:.0f}% within, {s['over']:.0f}% over)" if s else 'structure -'
    return f'{label} judge band {flat} {structure}'
