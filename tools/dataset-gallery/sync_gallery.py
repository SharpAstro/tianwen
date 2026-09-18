"""Render whatever the batch has finished and stage it into the gallery folder.

    python sync_gallery.py <store> <enhance outdir> <gallery dir>

Safe to run repeatedly while the batch is still going: it renders only masters that have an enhanced
FITS and whose card's images are older than that FITS, so a partial rollout costs only the cards that
actually changed. Prints the ids it touched, which is what the Artifact publish then passes as files.
"""
import io
import json
import os
import subprocess
import sys


def rows_of(page):
    s = io.open(page, encoding="utf-8").read()
    i = s.find('[{"slug"')
    depth, j = 0, i
    while j < len(s):
        if s[j] == '[':
            depth += 1
        elif s[j] == ']':
            depth -= 1
            if depth == 0:
                break
        j += 1
    return s, i, j, json.loads(s[i:j + 1])


def main():
    store, outdir, gallery = sys.argv[1], sys.argv[2], sys.argv[3]
    page = os.path.join(gallery, "index.html")
    s, i, j, rows = rows_of(page)
    enhanced = os.path.join(outdir, "enhanced")
    done = {f[:-5] for f in os.listdir(enhanced) if f.endswith(".fits")}

    stale = []
    for r in rows:
        if r["name"] not in done:
            continue
        src = os.path.join(enhanced, r["name"] + ".fits")
        card = os.path.join(gallery, "img", "%d.jpg" % r["id"])
        if not os.path.exists(card) or os.path.getmtime(card) < os.path.getmtime(src):
            stale.append(r)
    if not stale:
        print("nothing to do: every finished master is already staged")
        return

    print("rendering %d card(s): %s" % (stale and len(stale), ", ".join(str(r["id"]) for r in stale)))
    tmp_rows = os.path.join(outdir, "_sync_rows.json")
    json.dump(stale, open(tmp_rows, "w", encoding="utf-8"), separators=(",", ":"))
    here = os.path.dirname(os.path.abspath(__file__))
    rc = subprocess.call([sys.executable, os.path.join(here, "render_pairs.py"),
                          tmp_rows, store, enhanced, os.path.join(gallery, "img")])
    if rc != 0:
        raise SystemExit("render_pairs failed (%d)" % rc)

    # Carry back pairH, which changes with the crop, and rewrite the page's row array in place.
    updated = {r["id"]: r for r in json.load(open(tmp_rows, encoding="utf-8"))}
    for r in rows:
        if r["id"] in updated:
            r["pairH"] = updated[r["id"]]["pairH"]
    io.open(page, "w", encoding="utf-8", newline="\n").write(
        s[:i] + json.dumps(rows, separators=(",", ":")) + s[j + 1:])
    os.remove(tmp_rows)

    files = {}
    for r in stale:
        for suffix in (".jpg", "c.png"):
            rel = "img/%d%s" % (r["id"], suffix)
            if os.path.exists(os.path.join(gallery, rel)):
                files[rel] = os.path.join(gallery, rel).replace("\\", "/")
    print("\nPUBLISH_FILES " + json.dumps(files))


if __name__ == "__main__":
    main()
