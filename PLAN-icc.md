# PLAN-icc.md — ICC profile support across our image I/O paths

## Goal

Tag the file formats we write so colour-managed consumers (Photoshop,
Lightroom, browsers, mobile apps) interpret the bytes correctly. Cover the
three output formats we actually produce: **PNG, JPEG, TIFF**.

## Scope decisions (locked)

1. **Display PNG / JPEG / 8-bit TIFF**: tag as **sRGB v4**. Same single
   bundled profile (`SharpAstro.Color.Icc.IccProfiles.SRgbV4`) used everywhere.
2. **Scientific 32-bit-float TIFF** (`Image.WriteTiffAsync`): **leave
   untagged**. Scientific consumers (PixInsight, tifffile, ImageJ) treat
   untagged float as scene-linear, which is what we want. The old Magick.NET
   path tagged sRGB on this data, but the data wasn't sRGB-TRC encoded — it
   was a lie that no consumer noticed. Honest-untagged is the better contract.
3. **JPEG embedding via post-encode injection**, not encoder fork. APP2
   markers can be inserted between SOI and SOF without touching the
   encoder. Single-segment only (our 588-byte profile fits in one APP2;
   multi-segment is straightforward to add later if needed).
4. **PNG** already supports ICC via `PngWriter.Encode(..., iccProfile)`. No
   library work needed; just pass the profile at every display call site.

## Phasing

### Phase 1 — `SharpAstro.Tiff` 3.0 (breaking)

Change `TiffPageOptions.IccProfile` from `byte[]?` to `ReadOnlyMemory<byte>`
so callers can pass `IccProfiles.SRgbV4` directly. Major version bump per
semver — zero in-tree consumers, low blast radius.

- `src/SharpAstro.Tiff/TiffPageOptions.cs:52` — type change.
- `src/SharpAstro.Tiff/TiffWriter.cs:147-148` — adapt the consumer:
  `if (!options.IccProfile.IsEmpty) ifd.SetUndefined(tag, options.IccProfile.ToArray());`
- `tests/SharpAstro.Codecs.Tests/TiffReaderRoundTripTests.cs:120` — drop the
  `.ToArray()` at the call site.
- Bump `VersionPrefix` / `AssemblyVersion` to **3.0.0**.

### Phase 2 — `SharpAstro.Jpeg` 1.0 (new)

Pure-managed library, single API:

```csharp
public static class JpegIccInjector
{
    public static byte[] EmbedIccProfile(ReadOnlySpan<byte> jpeg, ReadOnlyMemory<byte> profile);
}
```

Mechanics: walk JPEG markers after SOI, skip existing APPn segments by
reading their big-endian length prefix, stop at the first non-APP marker
(DQT / DHT / SOFn), insert an APP2 segment with `ICC_PROFILE\0` + `seq=01` +
`total=01` + profile bytes, append the rest verbatim.

Reject inputs > 65517 bytes for v1 (single-segment limit). Multi-segment can
be added when someone hands us a non-trivial device profile.

- `src/SharpAstro.Jpeg/SharpAstro.Jpeg.csproj` — new project, AOT-compat,
  no native deps.
- `src/SharpAstro.Jpeg/JpegIccInjector.cs` — the implementation.
- `tests/SharpAstro.Codecs.Tests/JpegIccInjectorTests.cs` — round-trip:
  encode a tiny JPEG via `StbImageWriteSharp` → inject sRGB v4 → parse APP2
  back out → byte-compare with the input profile.

### Phase 3 — Publish

Single PR on `../sharpastro/StbImageSharp` with both libraries. Push, let
upstream CI publish, poll NuGet until both `SharpAstro.Tiff 3.0.*` and
`SharpAstro.Jpeg 1.0.*` resolve.

(Per `feedback_no_push_before_nuget.md`: don't push the TianWen side until
both packages are confirmed published.)

### Phase 4 — TianWen call sites

**4a — Test display-TIFF helper.** Three inlined 8-bit RGB TIFF writers
share the same shape and all want the same sRGB tag:

- `src/TianWen.Lib.Tests/GpuStretchPipelineTests.cs:562`
- `src/TianWen.Lib.Tests/VkRendererPrimitiveTests.cs:248`
- `src/TianWen.Lib.Tests/StretchTests_NewPipeline.cs:273`

Extract to `src/TianWen.Lib.Tests/Helpers/TestDisplayTiffWriter.cs` and
embed `IccProfiles.SRgbV4` via the new `ReadOnlyMemory<byte>` API.

**4b — `NinaImageEndpoints` JPEG.** `src/TianWen.Hosting/Api/NinaV2/NinaImageEndpoints.cs:126`
wraps `WriteJpg` output through `JpegIccInjector.EmbedIccProfile(..., IccProfiles.SRgbV4)`.

**4c — `Image.WriteTiffAsync` stays untagged** (scope decision 2).

**4d — Production PNG writers.** If any production code path writes display
PNGs (vs the test-only call sites currently in `Cr2ImportTests`, etc.),
audit + tag. As of writing there are none in `TianWen.Lib` proper, only in
tests + `tools/`. Test PNG outputs can be tagged via a helper analogous to
4a, or left alone since they're regression artifacts not user-facing data.

## Non-goals

- **No ICC consumption on read.** `TryReadTiff` will continue to ignore
  `page.IccProfile`. Astronomy inputs (FITS, CR2, CR3, scientific TIFF) use
  our own colour management (`CameraToSrgbMatrix` / `FilterCurveDatabase`)
  and don't need ICC transforms. If we ever start ingesting third-party
  RGB content this becomes worth revisiting.
- **No `Image.WriteTiffAsync` profile tag.** See scope decision 2.
- **No linear-sRGB profile in `SharpAstro.Color.Icc`.** Only needed if we
  change our mind on scope decision 2.
- **No multi-segment APP2 support in v1.** Single 588-byte sRGB profile
  fits easily; revisit when we have a use case for larger profiles.

## Risk

Low. New code paths gated behind explicit opt-in (a non-default
`IccProfile` field on TIFF, an explicit call to `JpegIccInjector` on JPEG).
The breaking change on `SharpAstro.Tiff` has zero in-tree consumers — the
NuGet ecosystem at large doesn't consume this package yet, so the major
bump is essentially a free reset.
