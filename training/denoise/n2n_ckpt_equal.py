"""Compare two trainer checkpoints on what the trainer controls, and nothing else.

Every tensor must be bit-identical (same keys, shapes, dtypes and bytes) and every metadata key
BOTH files carry must agree. A key only one side has (the trainer grew an option and now records
it) is printed and does not fail the comparison. Prints IDENTICAL and exits 0, or DIFFERENT and
exits 1; any other exit is an error (a file missing or unreadable).

Never compare checkpoints by file hash. torch.save writes a zip archive whose members are named
after the OUTPUT FILE's stem (n2n_v19d_s2/data.pkl), whose storages are padded to a 64-byte
alignment that shifts with those names, and whose pickle holds whatever metadata dict the trainer
passed; so identical weights saved under another name, or by a trainer that records one more key,
hash differently by construction. E0 (2026-09-02) hit exactly that: 813,251 identical parameters
in both checkpoints, DIFFERENT by hash, because of the `pair_time` key and a five-character-longer
stem. Re-saving the reference with that key under the reproduction's name gave the reproduction's
hash exactly.

    python n2n_ckpt_equal.py [--cache DIR] REFERENCE.pt REPRODUCED.pt
"""
import argparse
import os
import sys

import torch


def bits_equal(a, b):
    """Bytewise equality, so NaN == NaN and -0.0 != +0.0, which is what 'the same weights' means."""
    if a.dtype != b.dtype or a.shape != b.shape:
        return False
    return torch.equal(a.contiguous().reshape(-1).view(torch.uint8),
                       b.contiguous().reshape(-1).view(torch.uint8))


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("reference")
    p.add_argument("reproduced")
    p.add_argument("--cache", default=None,
                   help="directory both names are relative to (default: the names as given)")
    a = p.parse_args()
    paths = [os.path.join(a.cache, n) if a.cache else n for n in (a.reference, a.reproduced)]
    ref, rep = (torch.load(q, map_location="cpu", weights_only=False) for q in paths)

    different = False
    meta_ref = {k: v for k, v in ref.items() if k != "model"}
    meta_rep = {k: v for k, v in rep.items() if k != "model"}
    for k in sorted(set(meta_ref) & set(meta_rep)):
        same = meta_ref[k] == meta_rep[k]
        different |= not same
        print(f"  meta {k:18s} {'same' if same else 'DIFFERS'}  {meta_ref[k]!r}"
              + ("" if same else f"  vs  {meta_rep[k]!r}"))
    for side, only in (("reference", set(meta_ref) - set(meta_rep)),
                       ("reproduced", set(meta_rep) - set(meta_ref))):
        for k in sorted(only):
            src = meta_ref if side == "reference" else meta_rep
            print(f"  meta {k:18s} only in {side} (informational): {src[k]!r}")

    sa, sb = ref["model"], rep["model"]
    keys_a, keys_b = list(sa), list(sb)
    if set(keys_a) != set(keys_b):
        different = True
        for k in sorted(set(keys_a) - set(keys_b)):
            print(f"  tensor {k}: only in reference")
        for k in sorted(set(keys_b) - set(keys_a)):
            print(f"  tensor {k}: only in reproduced")
    shared = [k for k in keys_a if k in sb]
    n_equal = 0
    n_params = 0
    for k in shared:
        ta, tb = sa[k], sb[k]
        n_params += ta.numel()
        if bits_equal(ta, tb):
            n_equal += 1
            continue
        different = True
        if ta.dtype != tb.dtype or ta.shape != tb.shape:
            print(f"  tensor {k}: {ta.dtype} {tuple(ta.shape)} vs {tb.dtype} {tuple(tb.shape)}")
        else:
            d = (ta.double() - tb.double()).abs()
            print(f"  tensor {k}: {int((d > 0).sum())} of {ta.numel()} elements differ, "
                  f"max |diff| {float(d.max()):.3e}")
    print(f"  tensors {len(shared)} shared, {n_equal} bit-identical, {n_params} parameters; "
          f"metadata keys shared {len(set(meta_ref) & set(meta_rep))}")
    verdict = "DIFFERENT" if different else "IDENTICAL"
    print(f"  {verdict}  {paths[0]}  vs  {paths[1]}")
    sys.exit(1 if different else 0)


if __name__ == "__main__":
    main()
