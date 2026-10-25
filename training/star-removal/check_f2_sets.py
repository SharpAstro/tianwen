"""Checks f2-sets.json (F2, #1827), which sits beside this file: the three master-stretched sets are recorded, each stretched from the
master, with tiles, plain and random of one size, and none on E:. Exits non-zero naming what failed.

    python -I training/star-removal/check_f2_sets.py
"""
import json
import pathlib
import sys

REQUIRED = ("path", "stretch", "commit", "args", "sessions", "draws", "tiles")
failures = []


def check(ok, what):
    if not ok:
        failures.append(what)


def on_e(text):
    return isinstance(text, str) and text.replace("\\", "/").lstrip().lower().startswith("e:")


def walk(value, where):
    if isinstance(value, dict):
        for k, v in value.items():
            walk(v, f"{where}.{k}")
    elif isinstance(value, list):
        for i, v in enumerate(value):
            walk(v, f"{where}[{i}]")
    elif on_e(value):
        failures.append(f"{where} is on E: ({value})")


path = pathlib.Path(__file__).with_name("f2-sets.json")
try:
    doc = json.loads(path.read_text(encoding="utf-8"))
except (OSError, ValueError) as e:
    print(f"f2-sets.json: cannot be read: {e}")
    sys.exit(1)

sets = doc.get("sets", {})
for name in ("random", "plain", "lobed"):
    entry = sets.get(name)
    if not isinstance(entry, dict):
        failures.append(f"sets.{name} is missing")
        continue
    for key in REQUIRED:
        check(entry.get(key) not in (None, "", [], 0), f"sets.{name}.{key} is missing or empty")
    check(entry.get("stretch") == "master", f"sets.{name}.stretch is {entry.get('stretch')!r}, not 'master'")
    check(isinstance(entry.get("tiles"), int) and entry["tiles"] > 0, f"sets.{name}.tiles is not positive")
    check(isinstance(entry.get("draws"), int) and entry["draws"] > 0, f"sets.{name}.draws is not positive")
    args = entry.get("args", [])
    check("--stretch-from" in args and args[args.index("--stretch-from") + 1:][:1] == ["master"],
          f"sets.{name}.args does not state --stretch-from master")
    check(("--star-lobes" in args) == (name == "lobed"), f"sets.{name}.args {'lacks' if name == 'lobed' else 'carries'} --star-lobes")

if "random" in sets and "plain" in sets:
    for key in ("tiles", "draws", "sessions"):
        check(sets["random"].get(key) == sets["plain"].get(key), f"random and plain differ in {key}")

check(bool(doc.get("scorecard_config", {}).get("path_on_de1")), "scorecard_config.path_on_de1 is missing")
check(bool(doc.get("decision_sessions")), "decision_sessions (the 7 against 8 sessions) is not recorded")
check(bool(doc.get("checks")), "checks is empty")
walk(doc, "f2-sets")

if failures:
    print("f2-sets.json fails:")
    for f in failures:
        print(" -", f)
    sys.exit(1)
print(f"f2-sets.json: random / plain / lobed, stretch master, {sets['random']['draws']} draws and {sets['random']['tiles']} tiles each, none on E:")
