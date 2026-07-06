# SharpAstro.Codecs — an image sniff+dispatch facade over the codec family (plan)

**Status: NOT STARTED (design captured).** Motivated by the `PngImage.ToRgba8()` release
(SharpAstro.Png `3.4.431`) and the Console.Lib markdown-image work, which together produced two
ad-hoc prototypes of a facade that does not exist yet, plus a recurring version-skew footgun. Turn
those prototypes into a proper two-package layer in the Codecs repo, so consumers reference
one thing, sniff+dispatch lives in one place, and each codec implements a shared interface.

## Motivation — what already exists proves the shape

Two pieces of "the facade" are already written, just in the wrong places:

1. **`SharpAstro.Png.PngImage.ToRgba8()`** — the first *shared codec capability*, hoisted out of a
   consumer's private helper into the codec package (PR #10, shipped in `3.4.431`).
2. **`Console.Lib/ImageDecoder.cs`** — a magic-byte sniffer + dispatcher (`internal`, PNG+JPEG only,
   hardcoded `if`-chain) that decodes `![alt](url)` bytes and returns tightly-packed 8-bit RGBA. It
   even documents that it "replaces a dependency on the general-purpose stb_image port."

tianwen's own `Image.Import.cs TryReadImageFile` is a third, extension-based dispatcher (no Magick.NET
fallback) that delegates CR2/CR3 to FC.SDK and FITS to FITS.Lib. So the *same* "detect a format and
route it to a decoder" logic is written three times, none reusable.

### The version-skew this dissolves

Every consumer cherry-picks individual codec packages and floats each at `3.4.*` independently. That
lets them drift: tianwen resolves `SharpAstro.Png 3.4.431` (direct) but transitively drags in
`SharpAstro.Jpeg 3.4.411` via `FC.SDK.Raw 1.4.501`'s baked nuspec pin (nuspecs can't float — pack
snapshots the concrete build-time version). Harmless this time (identical source), but the class of
bug is real. If consumers reference **only** the facade, the whole family arrives at the single
version the facade was built against (internal ProjectReference = exact pin = lockstep). One dep, one
bump, no skew.

## Goal / conventions (mirror the SER.Lib / Lzip.Lib pattern)

- **Two new packages, both in the existing Codecs repo** (they ship in the `3.x` family lockstep,
  same `VERSION_PREFIX`, same CI):
  - **`SharpAstro.Codecs.Abstractions`** — the shared interface + common image type. Zero third-party
    deps, `IsAotCompatible`. Matches the existing `SharpAstro.Codecs.Tests` naming.
  - **`SharpAstro.Codecs`** — the facade: depends on every codec package + Abstractions; owns the
    signature registry + sniff/dispatch (the promoted `ImageDecoder`).
- **Name rationale:** `SharpAstro.Codecs`, NOT `SharpAstro.Imaging` — `Imaging` is already
  `TianWen.Lib.Imaging`, so the `Imaging` name would blur the consumer/library line.
- **Scope discipline ("does one job well"):** detect a raster format from its bytes and hand back a
  fidelity-preserving decoded image. NOT an astro-domain image type (WCS / capture time / camera
  matrix stay on tianwen's `Image`), NOT a UI raster (that stays DIR.Lib's `RgbaImage`).

## Public API (shape)

```csharp
// SharpAstro.Codecs.Abstractions
public enum SampleFormat { UInt8, UInt16, Float32 }

public interface IDecodedImage
{
    int Width  { get; }
    int Height { get; }
    int Channels { get; }              // 1 (gray) / 3 (RGB) / 4 (RGBA)
    SampleFormat SampleFormat { get; } // fidelity tier: 8/16/float
    ReadOnlySpan<byte> Pixels { get; } // row-major, tightly packed, host byte order
    ReadOnlySpan<byte> IccProfile { get; }  // empty when absent
    // convenience down-convert for the display tier (Console.Lib -> RgbaImage):
    byte[] ToRgba8();
}

public interface IImageDecoder
{
    static abstract bool CanDecode(ReadOnlySpan<byte> header);              // magic-byte sniff
    static abstract bool TryDecode(ReadOnlySpan<byte> bytes, out IDecodedImage image);
}
```

```csharp
// SharpAstro.Codecs (facade)
public static class ImageCodecs
{
    // AOT-safe explicit registry (no reflection scan). Order = sniff order.
    public static bool TryDecode(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out IDecodedImage? image);
    public static bool TryDetectFormat(ReadOnlySpan<byte> header, out ImageFormat format);
}
```

Each codec package (`SharpAstro.Png`, `.Jpeg`, `.Tiff`, `.Exr`, `.Jxr`, `.Jxl`) takes a dependency on
`SharpAstro.Codecs.Abstractions` and implements `IImageDecoder`, returning a small concrete
`RasterImage : IDecodedImage`. `ToRgba8()` (already shipped for PNG) becomes the canonical
down-convert on the interface.

## The two-tier image question (the part that needs care)

`IDecodedImage` must serve both:
- **Display tier** — Console.Lib -> DIR.Lib `RgbaImage`, 8-bit. Only needs `ToRgba8()`.
- **Fidelity tier** — tianwen -> float `Image`. Needs bit depth, channel layout, raw samples, ICC.

So the interface exposes the fidelity surface and offers `ToRgba8()` as convenience. It must NOT grow
astro metadata or it becomes a second `Image`.

## Boundary rule (load-bearing)

**The facade must not depend on DIR.Lib.** Dependencies point one way only:

```
SharpAstro.Codecs.Abstractions   (IImageDecoder, IDecodedImage; zero deps, AOT)
        ^                    ^
SharpAstro.Png/.Jpeg/.Tiff/...    |   each implements IImageDecoder -> RasterImage
        ^                    |
SharpAstro.Codecs  -----------+   (facade: explicit registry + sniff/dispatch)
        ^
consumers:  Console.Lib (-> RgbaImage adapter)   tianwen (-> Image adapter)   FC.SDK
```

`RgbaImage` stays DIR.Lib's display raster; the `IDecodedImage -> RgbaImage` adapter lives at the
Console.Lib boundary (it already builds `RgbaImage` from the `byte[]`). Do NOT make `RgbaImage`
implement `IDecodedImage` or make DIR.Lib pull the codec stack.

## Consumer migration

- **Console.Lib** — delete `internal ImageDecoder`, reference `SharpAstro.Codecs`, call
  `ImageCodecs.TryDecode` + adapt to `RgbaImage`. (Its in-flight WIP already drops the private
  `ToRgba8` helper for the library one; this is the next step past that.)
- **tianwen** — `TryReadImageFile` delegates the raster formats to `ImageCodecs.TryDecode` (+ an
  `IDecodedImage -> Image` adapter), keeps CR2/CR3 (FC.SDK) and FITS (FITS.Lib) special-cased on top.
  Replaces residual Magick.NET on the read path.
- **FC.SDK** — can reference the facade instead of individual codec packages (removes the skew source).

## Phasing

| Phase | Scope | Done when |
|------|-------|-----------|
| 1 | `SharpAstro.Codecs.Abstractions`: `IDecodedImage`, `SampleFormat`, `RasterImage`, `IImageDecoder` | package builds, AOT-clean, unit-pinned |
| 2 | Implement `IImageDecoder` on PNG + JPEG; `SharpAstro.Codecs` facade with explicit 2-codec registry | `ImageCodecs.TryDecode` round-trips PNG+JPEG in `SharpAstro.Codecs.Tests` |
| 3 | Migrate Console.Lib onto the facade (delete `ImageDecoder`) | Console.Lib markdown images render via facade; CI green |
| 4 ✅ | Add TIFF / EXR / JXR / JXL decoders to the registry | **DONE** (Codecs 3.5 — full-family registry, plus new JPEG XL + `SharpAstro.Jpeg.GainMap` Ultra HDR members) |
| 5 ✅ | tianwen `TryReadImageFile` falls back to `ImageCodecs.TryDecode` (+ `Image` adapter via `ToFloats()`) for PNG / JPEG / JXR / EXR / JXL | **DONE** — `Image.Import.TryReadViaCodecs`; `CodecsFacadeImportTests` green. TIFF / CR2 / CR3 / FITS stay on their bespoke metadata-carrying paths (facade `IDecodedImage` has no structured EXIF / Bayer / FITS); read-path Magick.NET was already retired pre-facade |
| 6 | FC.SDK references facade instead of individual codecs | NOT STARTED — skew source removed |

**Status (2026-07-06):** Phases 1–5 shipped. The read fallback is intentionally *additive* — it fills the
PNG/JPEG/JXR/EXR/JXL gap (so tianwen can reopen the previews + HDR masters it writes) without touching the
astro-critical TIFF/CR2/CR3/FITS readers. tianwen keeps its **direct** codec refs (writers + `Color.Icc` /
`Exif` / `Jpeg.IccInjector`, none of which the facade depends on), so this is *not* the "ref only the facade"
consolidation Console.Lib did. Deferred: honour `IDecodedImage.ColorEncoding` (linearise PQ/HLG/non-sRGB HDR
on ingest instead of trusting the `[0,1]` container convention); Phase 6 (FC.SDK → facade); and a gain-map
JPEG *export* path (Ultra HDR delivery for tianwen's HDR previews, distinct from this read work — now
*unblocked* by the Codecs 3.6 baseline JPEG encoder + `SharpAstro.Jpeg.GainMap` `Compute`/`Assemble`; only
the tianwen-side render wiring remains, tracked in [`../todo/imaging.md`](../todo/imaging.md)).

## Open questions

1. **Registry mechanism.** Static-abstract `IImageDecoder` is zero-alloc and clean, but AOT forbids
   reflection-scanning for implementors, so the facade needs a **hand-maintained array** of the
   codecs. Alternative: plain instance interface + a registered `IImageDecoder[]` (trivially
   enumerable, one instance per codec). Leaning static-abstract + explicit registry to match the
   codebase's no-reflection/AOT grain.
2. **`IDecodedImage` fidelity surface.** Exact fields for 16-bit / float / indexed / gray so both tiers
   are served without leaking astro concerns. Is `IccProfile` enough, or do we also carry an EXIF blob?
3. **Multi-signature formats.** TIFF (`II*\0` / `MM\0*`) and JXR share a lead; `CanDecode(header)` must
   take enough bytes to disambiguate. Define the header-window contract.
4. **Where the plan/code lives.** Packages ship from the Codecs repo; this plan doc lives in
   tianwen's `docs/plans/` (the coordinating consumer + established planning home).
