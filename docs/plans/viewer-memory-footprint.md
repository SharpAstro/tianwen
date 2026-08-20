# Viewer memory footprint: the two buffers beside the pixels

**Priority: HIGH.** Opening one large document costs ~2.5 GB, and ~0.5 GB of that never comes back
for the life of the process.

## The measurement

`A02-01-Project Information Drawing Standards-B mkd.tiff` -- an architectural drawing, **13228 x 9354
x 3ch**, 8-bit RGB, Deflate + Predictor 2, 585 strips of 16 rows, 5.7 MB on disk. That is
**123,734,712 px**, and the viewer's working set while it is open is about 2.5 GB.

The number is not a leak. It is what the current pipeline costs, and it decomposes exactly:

| Buffer | Bytes/px | This doc | Lifetime |
|---|---:|---:|---|
| Decoded TIFF raster (`TiffDocument.Pages[0].Pixels`, contig 8-bit RGB) | 3 | 354 MiB | transient, but alive *while* the float planes are filled |
| The float `Image` -- one `float[h,w]` per channel | 12 | 1,416 MiB | while the document is open |
| Host-visible staging buffer (`VkFitsImagePipeline._stagingBuffer`) | 4 | 472 MiB | **process lifetime** |
| Histograms (3 x 2048 floats) | -- | 24 KiB | -- |
| **Peak while loading** | **19** | **2.19 GiB** | |
| Steady state once the raster is collected | 16 | 1.84 GiB | |

Plus device memory: three `R32Sfloat` images, **12 B/px = 1.38 GiB**. On a shared-memory GPU (the
Snapdragon dev box) that comes out of the same physical pool, so the real peak is ~3.57 GiB.

`19 x 123,734,712 = 2,350,959,528` -- 2.19 GiB, or 2.35 GB decimal. Add the 57 MB executable, SDL,
onnxruntime and the font atlas and the observed 2.5 GB is fully accounted for.

Extrapolate with `peak bytes ~= px x (src_bytes_per_px + 4 x channels + 4)`: this doc at 16-bit would
need 3.1 GiB, 16-bit mono would need 10 B/px.

## Why these are three problems, not one

The 12 B/px is the data model. `TianWen.Lib.Imaging` is float throughout, deliberately -- an
astronomical frame genuinely is float, and every stage from calibration to stretch assumes it. The
other two terms are not the data, they are how it gets there.

**M1 is a permanent cost with a transient cause.** `EnsureStagingBuffer` is grow-only: it reallocates
when a bigger upload arrives and is freed **only in the dispose path**. So one 472 MiB channel upload
high-water-marks the buffer, and every subsequent small FITS still carries it. Nothing shrinks it, and
nothing reports it -- a `VkBuffer` is invisible to the GC, so it does not even show as managed
pressure.

**M2 is a peak cost.** The raster and the float planes are both fully resident during conversion,
because `TiffReader.Read` returns a *complete* raster and `DecodeTiffPixels` then walks it. Yet the
reader already decodes **strip by strip** (585 of them here, 16 rows each) -- the whole-raster
intermediate is an API shape, not a requirement of the format.

**M3 is the floor itself, and it is viewer-local rather than a change to `Imaging`.** This is the
important distinction: making `Image` polymorphic over bit depth would touch every consumer of
`GetChannelSpan`, the whole stretch pipeline and the stacker, and is not worth it -- the pipeline is
right to be float. But **the viewer is not the pipeline.** It displays; it does not calibrate,
integrate or deconvolve. For an 8-bit display document it converts 354 MiB of samples into 1,416 MiB
of floats, uploads them, and then asks the GPU to sample them back into normalised floats -- which is
what a `R8Unorm` texture would have done for free.

So M1 changes what the process holds forever, M2 changes how high it spikes, and M3 changes the floor
for the one consumer that does not need the float model. They are independent and land in that order
of increasing cost.

## M1. Release the staging buffer when a burst of uploads ends -- SHIPPED

`tianwen`: `src/TianWen.UI.Shared/VkFitsImagePipeline.cs` plus one call site. Nothing in `Codecs`.

Add `TrimStagingBuffer()` -- destroy the buffer, zero `_stagingSize` -- and call it from the
document-load path after the last channel is uploaded.

**Do NOT free it inside `UploadChannelTexture`.** That is the obvious shape and it is wrong: the live
preview path uploads a channel **per frame**, and a colour sensor's mosaic is one large channel (an
ASI2600 frame is ~104 MB), so freeing after every upload turns a stable allocation into an alloc/free
per frame on the hot path. Only the caller knows a burst has ended, which is why this is an explicit
trim and not a policy inside the upload.

A size cap ("keep <= 32 MiB, release anything larger") was considered and rejected for the same
reason: it cannot tell a document load from a large live frame, so it would churn exactly the path
that needs the buffer retained.

`UploadToImage` is synchronous -- stated at `VkFitsImagePipeline.cs:1027` and already relied on by the
placeholder-upload loop -- so releasing immediately after it returns needs no fence.

**Effect: 472 MiB (4 B/px) off the steady state, and the high-water mark stops following the process
around.** Peak during load is unchanged; the buffer is genuinely in use at that moment.

**MEASURED, A/B on the same document and the same build otherwise: 3948 MB without the trim, 3399 MB
with it -- 549 MB back**, a little more than the 495 MB predicted (the buffer is sized to the padded
memory requirement, not to the pixel bytes). The absolute figures are higher than the 2.5 GB recorded
above because this box has a shared-memory GPU and a Debug build, which is the ~3.57 GiB case the
device-memory note predicts.

**What ships is gated on `ViewerState.SourceGeneration`, not on the upload itself.** That distinction
is the whole implementation: `UploadDocumentTextures` is ALSO the per-frame path for a live camera
feed (`LiveSessionTab`, `GuiderTab`), so trimming on every call would be the alloc/free-per-frame
regression this section warns about one paragraph up. `SourceGeneration` already increments on each
source replacement in `ViewerController` and is untouched by the live path, so it separates "a
document loaded" from "another frame arrived" without adding a flag. Pinned by
`ViewerUploadScratchTrimTests`, whose load-bearing cases are the ones asserting NO trim -- ten live
frames and three re-uploads of one document -- both of which fail against an unconditional trim. The
release itself is pinned on a real device by
`GpuStretchPipelineTests.TrimmingTheStagingBufferReleasesItAndTheNextUploadStillWorks`, which also
covers re-uploading afterwards and a double trim.

## M2. Decode strips straight into the float planes

`Codecs`: `src/SharpAstro.Tiff/TiffReader.cs` -- a decode-into entry point beside `Read`, handing the
caller each decoded strip (page index, first row, row count, `ReadOnlySpan<byte>` of samples) instead
of accumulating them. The per-strip decode, the predictor inversion and the endian swap already happen
at that granularity; what changes is where the bytes go.

`tianwen`: `src/TianWen.Lib/Imaging/Image.Import.cs` -- `DecodeTiffPixels` becomes a per-strip
converter writing into the already-allocated `float[h,w]` planes. Existing behaviour stays put:
`MinIsWhite` inversion and CMYK -> RGB are whole-plane passes that run after the last strip, exactly
as now.

**Effect: 354 MiB (3 B/px) off the peak**, scaling with the document rather than being fixed overhead.

Two things this deliberately does not fix:

- **`File.ReadAllBytes` still reads the file whole.** That is 5.7 MB here and irrelevant, but an
  *uncompressed* TIFF of these dimensions is 354 MB on disk and the streaming win is then cancelled by
  the file buffer. A stream- or mmap-based reader is the complement and is a much larger change to
  `SharpAstro.Tiff`'s entry points.
- `ExifReader.FromTiff(bytes)` needs the header bytes, so it keeps its current input.

## M3. Let the viewer hold the source bit depth

The part that makes this tractable: **the shader does not care.** `texture()` on a `R8Unorm` or
`R16Unorm` sampled image returns a float in [0,1] exactly as `R32Sfloat` does, so `image.frag` and the
whole stretch chain are untouched -- the sampler performs the normalisation the CPU currently
materialises 1.4 GiB to precompute. Those formats also have *wider* linear-filtering support than
`R32Sfloat`, which the pipeline already probes for (`R32SfloatLinearFilterSupported`).

So the work is not in the shader, it is in three places:

1. **Texture format selection** in `VkFitsImagePipeline`, from the document's `BitDepth`. Per-channel,
   since `UploadChannelTexture` is already per-channel.
2. **What `AstroImageDocument` holds.** Today it holds a float `Image` and derives its median/MAD
   stats from it. Those statistics are computable from 8- or 16-bit samples directly (cheaper, in
   fact), so the question is whether the document can carry the raw raster plus stats instead of a
   float `Image` -- and what that does to the paths that legitimately need floats: **Enhance**
   (`SharpenPipeline` is float end to end) and plate solving.
3. **A fallback that is not a cliff.** The honest shape is that a display-only document keeps its
   source depth and materialises floats *on demand* for the operations that need them -- Enhance
   already writes a temp FITS, so it is closer to that model than it looks.

**Effect for this document: 12 B/px -> 3 B/px on the CPU and the same on the GPU**, i.e. 354 MiB
instead of 1,416 MiB resident, and 354 MiB instead of 1,383 MiB of device memory. For a 16-bit
astronomical frame it is 6 B/px instead of 12.

**This is the largest win and the least certain scope.** It needs its own design pass before it is
committed to; the entry here exists so the 12 B/px is recorded as a floor of *this design* rather than
a law, and so the reason it is viewer-local is written down.

## Phasing

| Phase | Items | Rationale |
|-------|-------|-----------|
| A | **DONE** -- M1 | Self-contained, one repo, no API design. Fixes the cost that persists across documents -- the one a user experiences as "it got slow after I opened that file". |
| B | M2 in `Codecs` | A new public API on `SharpAstro.Tiff` plus a release. Second, so A has shipped by the time the pin moves. |
| C | M2 in `tianwen` | Follows B's release; the codec family floats per minor, so the pin edit is one line. |
| D | M3 design | Needs a decision on what `AstroImageDocument` holds and how Enhance / plate solve get floats. Do not start it as an implementation. |

## Verification

**Measure, do not reason.** All three are invisible to a functional test -- the picture is identical
either way, which is exactly why none of this was noticed.

- **M1** -- DONE. The A/B above (same document, trim on vs off) is the measurement: 549 MB. Done that
  way rather than "open, close, open a small FITS" because with the trim in place the buffer is
  already gone by the end of the first load, so the small-FITS step has nothing left to reveal; the
  comparison has to be against a build without the trim. Headless assertions as planned, via the now
  public `StagingBufferSize`.
- **M2**: peak working set during the load of the same file, before and after; expect ~354 MiB lower.
  A unit test asserts the shape rather than the bytes: the decode-into path must produce a
  byte-identical `Image` to `Read`-then-convert for a predictor file, a `MinIsWhite` file and a CMYK
  file -- the three cases that post-process whole planes.
- **M3**: same working-set comparison, plus a pixel-identity check that an 8-bit document renders
  byte-identically through the `R8Unorm` path and the float path. If it does not, the sampler
  normalisation and `Image.StretchValue`'s normalisation have diverged, which is a real bug and not a
  tolerance to widen.
