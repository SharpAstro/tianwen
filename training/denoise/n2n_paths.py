"""Where this machine keeps the prepared caches and the bakes, stated once.

Every scorer used to carry its own `C:\\tianwen-scratch\\...` literals, so when the scratch root
moved (2026-08) three scripts kept a default pointing at a directory that no longer existed and
five more had `EVAL` constants nobody could run without editing them. The trainer's own `--cache`
is REQUIRED rather than defaulted, deliberately: a default that points at a sibling dataset is how
the v14 scoring ran cross-bake and reported a plausible table for the wrong tiles (see the note at
the top of n2n_metrics.py). The scorers name their arms by cache NAME and resolve them here.

Override the roots with TIANWEN_SCRATCH (the prepared caches: `tiles.f16` + `meta.json` + the `.pt`
checkpoints the trainer saves beside them) and TIANWEN_DATASETS (the bakes the caches were prepared
from, each with its `tiles-manifest.jsonl`).
"""
import os

SCRATCH = os.environ.get("TIANWEN_SCRATCH", r"C:\temp\tianwen-scratch")
DATASETS = os.environ.get("TIANWEN_DATASETS", r"D:\Astro-Dataset")


def cache(name):
    """A prepared cache under the scratch root, e.g. cache("n2n-d8") for the shipped arm."""
    return os.path.join(SCRATCH, name)


def bake(name):
    """A dataset bake under the datasets root, e.g. bake("2025-2026-darkscaled")."""
    return os.path.join(DATASETS, name)
