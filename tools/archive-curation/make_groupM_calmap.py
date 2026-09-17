"""The session-calibration-map.csv row for group M. Writes a fragment to C:/temp/e2 only; the dark
count is filled from the coverage matcher's own answer before the row is appended, never guessed."""
import csv
import sys

DARK_N = int(sys.argv[1]) if len(sys.argv) > 1 else None   # from the coverage matcher, after tagging

BIAS_VERDICT = (
    "ok; SAME NIGHT, gain 121 + offset 8 match, 140 frames at 32 us shot straight after the darks. "
    "Uncooled camera: CCD-TEMP 22.8-24.6 C against the lights' 20.7-22.8, which a bias does not care about"
)
DARK_VERDICT = (
    "ok; SAME NIGHT, exact camera + gain + offset + 10 s exposure, shot straight after the lights at "
    "20.7 C, the lights' own end temperature (lights ran 22.8 down to 20.7). MEASURED: median(dark - bias) "
    "+4/+5/+5 ADU R/G/B; light - dark below zero on at most 0.0001 percent of pixels over 12 frames across "
    "the night, p0.01 +149 to +187 ADU. SharpCap typed them FRAMETYP='Light'; relabelled Dark in the "
    "organized copy (CORRECTIONS.md), the pixels and the folder name agreeing"
)
FLAT_VERDICT = (
    "ok; NEXT MORNING (22:49 UTC, 7 h after the last light), 304 frames at 0.034 s, gain and offset match. "
    "Owner confirms the train was untouched overnight. MEASURED dust-free (flat / smoothed p0.1 0.987, "
    "pixels under 0.97 0.0001 percent), so it corrects vignetting only, 5.5 percent at the corner ring. "
    "A flat-against-sky falloff check is confounded on this field (Eta Carinae and the Milky Way fill "
    "15 degrees and lift the centre). SharpCap typed them FRAMETYP='Light'; relabelled Flat. Daylight "
    "flats, so their channel ratios describe the daylight as well as the filter. The camera is "
    "uncooled and warmed through the run (CCD-TEMP rounds to 28/29/30 C over 95/137/72 frames), and "
    "the master-group key splits by the degree, so the bake builds its flat from the 95 at 28 C; "
    "admitted by capture date (no TELESCOP or FOCALLEN on either side), 0.42 days from the first light"
)
DARKFLAT_VERDICT = (
    "ok; SAME MORNING, 300 frames at 0.034 s matching the flats exactly; dark-flat - bias 0 ADU"
)
FILTER_NOTE = (
    "UV-IR-Cut by the OWNER'S RECOLLECTION (uncertain, 2.5 years on), which the pixels do not contradict "
    "and cannot confirm: star colours on the same IMX294 read R/G 0.599 B/G 0.669 against 0.426-0.553 and "
    "0.511-0.586 on six QHY294C + IDAS-LPS-D3 nights, so broadband and not the D3"
)

with open(r'C:/temp/e2/groupM-calmap-row.csv', 'w', newline='', encoding='utf-8') as fh:
    w = csv.writer(fh)
    w.writerow([
        "UV-IR-Cut", "eta-Car-Nebula", "2024-02-03", 861, 10, 121, 8, 21.0,
        "BIAS/2024-02-03-g121-o8-t+24", 140, BIAS_VERDICT,
        "DARK/2024-02-03-g121-o8-t+21-e10s", DARK_N if DARK_N is not None else "", DARK_VERDICT,
        "UV-IR-Cut/2024-02-03/FLAT", 304, FLAT_VERDICT + ". FILTER: " + FILTER_NOTE,
        "UV-IR-Cut/2024-02-03/DARKFLAT", 300, DARKFLAT_VERDICT,
    ])
print('wrote C:/temp/e2/groupM-calmap-row.csv', '(dark_n pending the coverage matcher)' if DARK_N is None else '')
