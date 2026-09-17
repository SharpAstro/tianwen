"""Read a degradation cache's rows and compare the estimator's kernel with the drawn kernel's effective width."""
import json, sys, statistics as st

path = sys.argv[1]
rows = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
blur = [r for r in rows if r.get("Mode", "").lower().startswith("blur") or r.get("ExtraFwhmPx", 0) > 0]
print(f"rows {len(rows)}, blur rows {len(blur)}")
src = {}
for r in blur:
    src[r.get("KernelSource")] = src.get(r.get("KernelSource"), 0) + 1
print("KernelSource:", src)
ref = {}
for r in blur:
    if r.get("KernelSource") == "drawn":
        ref[r.get("KernelEstimateRefusal")] = ref.get(r.get("KernelEstimateRefusal"), 0) + 1
print("refusals:", ref)

est = [r for r in blur if r.get("KernelSource") == "estimated" and r.get("EffectiveKernelFwhmPx")]
def band(x):
    for lo, hi in ((1.0, 1.1), (1.1, 1.3), (1.3, 1.6), (1.6, 2.0), (2.0, 9.9)):
        if lo <= x < hi:
            return f"{lo:.1f}-{hi:.1f}x"
    return "?"
print(f"\n{'realised':>9} {'n':>3} {'est/eff p10':>11} {'p50':>6} {'p90':>6} | {'clean fit/hfd-width p50':>22} {'obs beta p50':>12}")
groups = {}
for r in est:
    ratio = r["ComposedFwhmPx"] / r["CleanFwhmPx"]
    groups.setdefault(band(ratio), []).append(r)
for b in sorted(groups):
    g = groups[b]
    q = sorted(r["EstimatedKernelFwhmPx"] / r["EffectiveKernelFwhmPx"] for r in g)
    pick = lambda p: q[min(len(q) - 1, int(p * len(q)))]
    cf = st.median(r["CleanFitFwhmPx"] / r["CleanFwhmPx"] for r in g)
    ob = st.median(r["ObservedFitBeta"] for r in g)
    print(f"{b:>9} {len(g):3d} {pick(0.1):11.3f} {pick(0.5):6.3f} {pick(0.9):6.3f} | {cf:22.3f} {ob:12.2f}")
print("\nper row (realised, clean fit, observed fit, est kernel vs effective, drawn):")
for r in sorted(est, key=lambda r: r["ComposedFwhmPx"] / r["CleanFwhmPx"]):
    print(f"  {r['ComposedFwhmPx']/r['CleanFwhmPx']:.3f}x  clean {r['CleanFitFwhmPx']:.2f} ({r['CleanFitBeta']:.1f})  obs {r['ObservedFitFwhmPx']:.2f} ({r['ObservedFitBeta']:.1f})  "
          f"est {r['EstimatedKernelFwhmPx']:.2f} vs eff {r['EffectiveKernelFwhmPx']:.2f} (drawn {r['ExtraFwhmPx']:.2f} b{r['MoffatBeta']:.1f})  ratio {r['EstimatedKernelFwhmPx']/r['EffectiveKernelFwhmPx']:.3f}")
