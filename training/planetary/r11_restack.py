"""R11 (#2116): every Jupiter, Saturn and Mars session of the planetary manifest stacked with its two halves, no sharpening, keep 0.5.

Glue only: the stacking is the CLI's (planetary stack --session --halves). Smallest session first. A session whose stack.log says
"done in" is not run again. A file restack.stop in the scratch folder ends the run between sessions. The corpus is only read.
"""
import json
import os
import re
import subprocess
import time

HERE = "D:/temp/tianwen-scratch/i2116/restack"  # scratch: one folder a session, the logs beside them
EXE = "D:/temp/tianwen-scratch/i2116/bin-batch/tianwen.exe"
MANIFEST = "D:/Astro-Dataset/planetary/manifest.json"


def say(text):
    with open(f"{HERE}/restack.log", "a", encoding="utf-8") as log:
        log.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S')} {text}\n")


sessions = [s for s in json.load(open(MANIFEST, encoding="utf-8"))["sessions"] if "venus" not in (s["name"] + s["folder"]).lower()]
sessions.sort(key=lambda s: s["frames"])
say(f"{len(sessions)} sessions, {sum(s['frames'] for s in sessions)} frames, build {open(os.path.dirname(EXE) + '/BUILT_FROM.txt').read().strip()}")
for s in sessions:
    if os.path.exists(f"{HERE}/restack.stop"):
        say("stopped by restack.stop")
        break
    out = f"{HERE}/{re.sub(r'[^A-Za-z0-9]+', '-', s['name']).strip('-')}"
    log = f"{out}/stack.log"
    if os.path.exists(log) and "done in" in open(log, encoding="utf-8", errors="replace").read():
        continue
    os.makedirs(out, exist_ok=True)
    say(f"start {s['name']} ({s['frames']} frames, {s['width']}x{s['height']} {s['colorId']})")
    began = time.time()
    with open(log, "w", encoding="utf-8") as f:
        code = subprocess.run([EXE, "planetary", "stack", "--manifest", MANIFEST, "--session", s["name"], "--halves", "--no-sharpen", "--keep", "0.5", "-o", out],
                              stdout=f, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW).returncode
    say(f"end {s['name']}: exit {code} in {time.time() - began:.0f} s")
else:
    say("all sessions done")
