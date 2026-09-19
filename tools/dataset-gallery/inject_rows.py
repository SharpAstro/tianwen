"""Render the gallery page: the tracked template plus a freshly built row table.

    python inject_rows.py <rows.json> <out .html> [--template gallery.html]

The page carries its rows inline as `window.MASTERS = [...]` on one line, so it opens with no fetch
and works from a file:// path as well as from the published artifact. That also means the data and
the markup drift apart silently: the page kept describing a 92-master bake, with fields from a
comparison feature that had been deleted, long after the store held 139.

So the table is REPLACED rather than hand-edited, and this refuses rather than guesses if the anchor
is not exactly one line -- a page that half-updated would render some cards against images that
belong to others, which looks like a rendering bug and is a data one.

THE TEMPLATE IS IN THE REPO (`gallery.html`, beside this script) and the page is a BUILD OUTPUT.
It used to be the other way round: the only copy of the markup lived in the git-ignored
`.artifact-gallery/`, so the card layout, the badges and the filters were untracked, unreviewable and
one `rm -rf` from gone -- and an edit to them could not be told apart from an edit to the data. The
output path is written fresh every run, so nothing there needs keeping.
"""
import io
import json
import os
import sys

ANCHOR = "window.MASTERS = "
DEFAULT_TEMPLATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gallery.html")


def main():
    rows_path, page_path = sys.argv[1], sys.argv[2]
    template = (sys.argv[sys.argv.index("--template") + 1] if "--template" in sys.argv
                else DEFAULT_TEMPLATE)
    rows = json.load(io.open(rows_path, encoding="utf-8"))
    page = io.open(template, encoding="utf-8", newline="").read()

    lines = page.split("\n")
    hits = [i for i, l in enumerate(lines) if l.lstrip().startswith(ANCHOR)]
    if len(hits) != 1:
        raise SystemExit(f"expected exactly one '{ANCHOR}' line in {template}, found {len(hits)}")

    # The ids the page addresses its images by must be dense and in row order, or a card reaches for
    # <id>_enhanced.png and gets another master's picture -- which reads as a rendering bug.
    ids = [r["id"] for r in rows]
    if ids != list(range(len(rows))):
        raise SystemExit(f"row ids must be 0..{len(rows) - 1} in order; got {ids[:8]}...")

    indent = lines[hits[0]][:len(lines[hits[0]]) - len(lines[hits[0]].lstrip())]
    lines[hits[0]] = indent + ANCHOR + json.dumps(rows, separators=(",", ":")) + ";"
    os.makedirs(os.path.dirname(os.path.abspath(page_path)), exist_ok=True)
    io.open(page_path, "w", encoding="utf-8", newline="").write("\n".join(lines))
    print(f"{len(rows)} rows -> {page_path} (template {os.path.basename(template)})")


if __name__ == "__main__":
    main()
