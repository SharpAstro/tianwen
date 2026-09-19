"""Replace the gallery page's embedded row table with a freshly built one.

    python inject_rows.py <rows.json> <index.html>

The page carries its rows inline as `window.MASTERS = [...]` on one line, so it opens with no fetch
and works from a file:// path as well as from the published artifact. That also means the data and
the markup drift apart silently: the page kept describing a 92-master bake, with fields from the
comparison feature that was deleted, long after the store held 139.

So the table is REPLACED from rows.json rather than hand-edited, and the script refuses rather than
guesses if the anchor is not exactly one line -- a page that half-updated would render some cards
against images that belong to others, which looks like a rendering bug and is a data one.
"""
import io
import json
import sys

ANCHOR = "window.MASTERS = "


def main():
    rows_path, page_path = sys.argv[1], sys.argv[2]
    rows = json.load(io.open(rows_path, encoding="utf-8"))
    page = io.open(page_path, encoding="utf-8", newline="").read()

    lines = page.split("\n")
    hits = [i for i, l in enumerate(lines) if l.lstrip().startswith(ANCHOR)]
    if len(hits) != 1:
        raise SystemExit(f"expected exactly one '{ANCHOR}' line in {page_path}, found {len(hits)}")

    before = lines[hits[0]]
    lines[hits[0]] = ANCHOR + json.dumps(rows, separators=(",", ":")) + ";"
    io.open(page_path, "w", encoding="utf-8", newline="").write("\n".join(lines))

    # The counts, because "it published" is not the same as "it published the right number of cards".
    old_len = before.count('"slug":')
    print(f"{page_path}: {old_len} rows -> {len(rows)} rows")
    ids = [r["id"] for r in rows]
    if sorted(ids) != list(range(len(rows))):
        raise SystemExit("row ids are not a dense 0..n-1 range; the page addresses images by id")
    print(f"ids 0..{len(rows) - 1} dense, {sum(1 for r in rows if r.get('solved'))} solved, "
          f"{sum(1 for r in rows if r.get('flip'))} flip-side")


if __name__ == "__main__":
    main()
