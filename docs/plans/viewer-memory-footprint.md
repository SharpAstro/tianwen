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

## M2. Decode strips straight into the float planes -- SHIPPED (tianwen half pending the pin)

`Codecs`: `src/SharpAstro.Tiff/TiffReader.cs` -- a decode-into entry point beside `Read`, handing the
caller each decoded strip (page index, first row, row count, `ReadOnlySpan<byte>` of samples) instead
of accumulating them. The per-strip decode, the predictor inversion and the endian swap already happen
at that granularity; what changes is where the bytes go.

`tianwen`: `src/TianWen.Lib/Imaging/Image.Import.cs` -- `DecodeTiffPixels` becomes a per-strip
converter writing into the already-allocated `float[h,w]` planes. Existing behaviour stays put:
`MinIsWhite` inversion and CMYK -> RGB are whole-plane passes that run after the last strip, exactly
as now.

**Effect: 354 MiB (3 B/px) off the peak**, scaling with the document rather than being fixed overhead.

**MEASURED DETERMINISTICALLY, and not the way this section first tried.** A working-set A/B said
"1564 MB off the peak" and was noise: the SAME unmodified build then measured 3362 MB steady in one
run and 2143 MB in another, so run-to-run variance exceeds 1200 MB and cannot resolve a 371 MB
change. `GC.GetAllocatedBytesForCurrentThread` around the decode can:
`TiffStreamingDecodeAllocationTests` shows the removed allocation is **exactly one raster** --
3,240,000 B at test size against a 12,960,000 B float-plane output, with the rest accounted for to
within 5 KB. Scaled to the document above that is 371 MB. The test fails when the whole-raster path
is restored, so it measures the change rather than restating it.

**`File.ReadAllBytes` is now the dominant term for uncompressed input**, which the note below
predicted and the test had to encode in its ceiling: for an uncompressed page the file IS
raster-sized, so M2 alone trades one for the other there. Closing it is the memory-mapping item.

**One bug found on the way, in `TiffWriter`.** It stamped the requested compression into the IFD while
its encoder switch fell through to the raw bytes, so asking for LZW wrote unlabelled-as-what-it-is
data: a file whose content and label disagree, which no reader can decode. It surfaced only because
the pixel-equality test above read 50 where it had written 3 -- two test suites had LZW cases passing
over it, since a corrupt file decodes to the same garbage through both readers and an equivalence
assertion then holds while asserting nothing. Fixed in SharpAstro.Tiff 3.11 by refusing what the
writer cannot apply.

Two things this deliberately does not fix:

- ~~**`File.ReadAllBytes` still reads the file whole.**~~ **DONE, and it was not the large change this
  predicted.** `TiffReader` already had a `Read(ReadOnlySpan<byte>)` overload whose own doc recommended
  mapping the file, so no `SharpAstro.Tiff` entry point had to move: `TryReadTiff` maps the file and
  passes the view. It matters exactly where predicted -- an uncompressed TIFF of these dimensions is
  354 MB on disk, so `ReadAllBytes` was handing back a byte[] as large as the raster M2 had just
  removed, trading one term for the other. Measured on the allocation test: the uncompressed case went
  19,444,760 B (raster + file) -> 16,204,608 B (M2, file only) -> **12,965,424 B**, against float
  planes of 12,960,000 B. The decode now allocates its own output plus 5.4 KB. Scaled to this
  document, that is 371 MB of raster plus 371 MB of file gone, leaving only the 1,485 MB of planes.
  The test's ceiling was tightened to exclude the file bytes and was seen to fail against the
  `ReadAllBytes` version, so it measures the mapping rather than assuming it.
- `ExifReader.FromTiff(bytes)` needs the header bytes, so it keeps its current input.

## M3. Let the viewer hold the source bit depth -- DESIGN

M1, M2 and the mapping removed everything that was not the data. What is left is the data model, and
for the reference document it is now the *entire* cost: **1,485 MB of float planes on the CPU and
1,485 MB of `R32Sfloat` device images**, 2,227 MB of the 2,251 MB measured. Nothing else is material.

The premise from the top of this file still holds -- `TianWen.Lib.Imaging` is right to be float, and
making `Image` polymorphic over bit depth is not worth it. **The viewer is not the pipeline.** It
displays; it does not calibrate, integrate or deconvolve. For an 8-bit document it converts 354 MiB
of samples into 1,416 MiB of floats, uploads them, and asks the GPU to sample them back into
normalised floats -- which is what an `R8Unorm` texture does for free.

### What the design turns on, and it is not the shader

The shader is a non-issue: `texture()` on `R8Unorm` / `R16Unorm` returns a float in [0,1] exactly as
`R32Sfloat` does, so `image.frag` and the whole stretch chain are untouched, and those formats have
*wider* linear-filtering support than `R32Sfloat` (which the pipeline already probes for).

What it turns on is **who still needs floats, and whether they need them resident**. Auditing the
consumers of `AstroImageDocument.UnstretchedImage`:

| Consumer | Needs | When |
|---|---|---|
| Most of the viewer chrome | metadata only (`ChannelCount`, `SensorType`, Bayer offsets) | always -- no pixels at all |
| GPU upload (`IPreviewSource.GetChannelData`) | pixels | always -- **but the sampler can normalise, so floats are not required** |
| Stretch stats (median / MAD / histogram) | pixels | once, at load -- computable from integer samples directly, and *cheaper* |
| **Star detection (`FindStarsAsync`)** | an `Image` | **automatically at every load** (`ViewerController.StartStarDetection`) |
| Colour calibration / auto-WB | pixels | on demand |
| Enhance (`SharpenPipeline`) | float end to end | on demand -- and it already round-trips a temp FITS |
| Plate solve | detected centroids, not pixels | on demand |
| Info-panel probe | one pixel | per frame, trivial either way |

**Star detection is the finding that shapes this.** It runs on every document open, not on demand, so
if it needs a float `Image` then the float planes exist anyway and M3 would save only the device half.
That single fact is the difference between a 1,114 MB win and a 2,227 MB one, and it is not visible
from the memory table at all.

### Options -- and the seam is NOT the CPU/GPU boundary

A first cut of this design split M3 at the CPU/GPU boundary (device-only, then CPU) and claimed the
device half "can ship before the CPU half and the CPU half does not redo it". **That was wrong**, and
the reason is worth keeping: to upload 8-bit you need 8-bit samples, and the document currently
converts to float and DISCARDS them. So a device-only change would have to re-quantise floats back to
bytes at upload time -- workable, lossless for an 8-bit source, and deleted again the moment the
document retains its raster. A throwaway mechanism.

The seam that actually works is **what the document HOLDS**, in two steps:

**D3' -- the document retains the source raster and uploads it directly.**

- Costs +3 B/px to hold, saves 9 B/px of device memory: **net -6 B/px**, with no re-quantise step.
  **Measured, not projected** (step 4, `GpuChannelFormatTests`, 2048x1536, driver-reported so
  alignment padding is in): RGB device 37,896,192 B -> 9,437,184 B = **-9.05 B/px**, held +3.00,
  **net -6.05 B/px**; mono **-3.02** device, +1.00 held, **net -2.02 B/px**. Only the DELTA is used,
  never either total, so a channel a case does not upload cancels out.
- No `IPreviewSource` change: the existing float accessor keeps working, because the float planes are
  still there. Only the upload path and the texture format move.
- Shippable alone, and it is exactly the data change D1' builds on rather than something D1' undoes.

**D1' -- stop keeping the float planes resident.** Demote them to a transient of the two passes that
need them at load (stretch stats, star detection), then release. Steady state falls to
**6 B/px** (raster + device) against 24 today; peak becomes 18 B/px.

**D2 -- remove even that transient** by making star detection depth-agnostic, which takes peak to
6 B/px as well. Gated: see the recommendation below.

### Where the work lands, concretely

- `VkFitsImagePipeline.UploadChannelTexture` / `CreateChannelTexture` -- **DONE.** The count above
  said six sites and was wrong: 1098 and 1124 are `CreateHistogramTexture`, whose float bin data
  must STAY `R32Sfloat`, and 443/1040 are its uploads. The channel sites are 263 (upload), 1015 (the
  1x1 placeholder), 1052 (imageCI) and 1085 (viewCI). Format is now per-channel state
  (`_channelFormats`) and **part of the recreate condition, not just the size** -- a texture cannot
  change texel format in place, so an 8-bit file opened after a float one at identical dimensions
  must still reallocate, or the copy reinterprets the new bytes through the old format and draws
  garbage at the right size. `ReadbackChannelFirstFloats` divides by 255 for a UNORM channel so the
  test diagnostic keeps returning [0,1] like the sampler does.
  Measured on an Adreno X1-85 at 256x256: **R8Unorm is 0.246 of R32Sfloat** (196,608 B against
  798,720 B over all three channels, driver-reported so alignment padding is included), via the new
  `ChannelDeviceBytes` -- the `StagingBufferSize` pattern, because a `VkImage` is invisible to both
  the GC and working set. Pinned by `GpuChannelFormatTests`; dropping the format term from the
  recreate condition fails all three.
  - **That test was order-dependent until step 4 caught it, and the failure mode is worth knowing:**
    `ChannelDeviceBytes` sums ALL THREE channels, so uploading only channel 0 measures it against
    whatever the other two happen to hold. It read 0.247 for a long time purely because nothing
    before it had uploaded anything big; adding a 2048x1536 case to the same shared fixture made it
    report **0.969**, i.e. the format change having achieved nothing. A per-channel claim must
    upload every channel, or difference two reads.
- `ImageRendererBase.UploadDocumentTextures` -- **DONE.** Each of the three upload sites (raw Bayer,
  3-channel composite, single-channel view) now tries `TryUploadRetainedRaster` first and falls back
  to the float span. Three things about the shape are load-bearing:
  - **The override IS the capability declaration.** The 8-bit upload is a virtual
    `TryUploadImageTexture(ReadOnlySpan<byte>, ...)` returning **false**, and there is no companion
    "supports 8-bit textures" flag anywhere. Five classes derive from `ImageRendererBase` --
    `VkImageRenderer` plus four offline test doubles -- so a new *abstract* member would have forced a
    stub into all four, each free to get it wrong; the false default keeps them on the float path
    unchanged, which `ViewerByteTextureUploadTests` asserts directly rather than leaving implied. The
    float-only double in that suite declines by NOT overriding the method, which is the shape those
    four suites are actually in -- a double that overrode it and returned false would test something
    else.
  - **Rejected: a `bool SupportsByteChannelTextures` beside the upload, and its natural successor, a
    `[Flags]` enum of supported texel formats.** The bool shipped first and was replaced the same day.
    The diagnosis behind the enum is right -- a bool named for one depth cannot say "8 but not 16", so
    a second format needs a second bool or a rename -- but a capability *set* is the wrong cure: it
    states every format TWICE (advertised, and implemented) and lets a backend claim a depth it never
    wrote, a disagreement nothing except a runtime throw can catch. That throw was in fact the whole
    guard behind the bool. Folding the claim into the method makes the lie unrepresentable and costs
    ONE member per format instead of two. It also buys the caller nothing to give up: a document
    retains exactly one depth (its source container width), so at most one raster lookup can ever
    succeed and there is nothing for a set to help choose between.
  - **The bool was additionally justified by an ordering that does not survive inspection**, which is
    worth recording because it read convincingly: "checked before the raster is looked for, because
    widening the bytes back to floats would be worse than never asking". Nothing widens anything on the
    fallback path -- the floats are already resident, which is the whole premise of D1' -- and the real
    short-circuit for a live source is the `source is not AstroImageDocument` test, which was already
    first. Removing the flag costs a float-only backend one `TryGetSourceRaster`: a field read and two
    bounds checks, per document change, not per frame.
  - **The single-channel site uploads source channel N into texture slot 0**, so the raster lookup
    takes the source index while the upload takes the slot. Passing the slot to both would upload
    channel 0's bytes while claiming to show blue.
  - Reaching the raster via `source is AstroImageDocument` needs no `IPreviewSource` change, and that
    is the interface's own documented pattern for document-only features (a SER frame has no retained
    raster and takes the float path). Safe because `IPreviewSource.GetChannelData` forwards to
    `UnstretchedImage`, the very image the raster hangs off -- and any transform that recomputes pixels
    builds a new `Image`, dropping the raster rather than carrying a stale one.
  - Verified end to end in `tianwen-fits` on a hand-written 8-bit TIFF (deliberately not written by
    `SharpAstro.Tiff`, so the importer meets a third-party layout): all five synthetic stars land at
    their computed screen positions, the diagonal gradient runs the right way (so no transposition or
    row offset), and the reported median matches the generator's expected mean. A cost probe cannot
    see pixels, so this half needed an eye on it.
- `AstroImageDocument` -- holds `UnstretchedImage` (`AstroImageDocument.cs:51`). D3' adds the retained
  raster beside it; D1' is what removes the float planes from that field.
- `Image.BitDepth` already carries the source depth, and `BitDepthEx.CarriesDisplayDataOnly` already
  answers "is this 8-bit" for the pre-stretch rule -- the same predicate selects `R8Unorm`.
- `TiffChannelSink` (`Image.Import.cs`) is where an 8-bit raster could be retained during the
  streaming decode at no extra cost, since the samples pass through it already.

### M3 prerequisite, found by measuring: `BitDepth` was carrying two facts

**`BitDepth` is the SOURCE container width -- and star detection was reading it as a statement about
sample SCALE. So every 8/16-bit document detected zero stars.** Fixed before D3' rather than
alongside it, because D3' leans harder on the container meaning (it picks `R8Unorm` from it) and
would have entrenched the conflation.

The importer normalises integer samples to `[0, 1]` but records the container width, so a 16-bit PNG
arrives as unit-referred samples labelled `Int16`. `Image.IsUnitScaledFloat` required
`BitDepth.Float32`, so that image binned into **two** histogram buckets (`threshold = round(1.0 *
0.91) + 1`), `Background` answered `bg=0 starLevel=1 noise=1.09`, and `FindStarsAsync` took its
"abnormal file" exit. The two rescalers could not paper over it: both early-return `this` when the
peak is already <= 1, so an image that arrives normalised never reaches the code that stamps
`Float32`.

Measured on ONE array of pixels, changing only the declared depth (`DocumentOpenCostProbe`, 6000x4000
with 3,000 planted Gaussian stars):

| declared `BitDepth` | hist threshold | detection level | stars found |
|---|---|---|---|
| `Float32` | 0.910 | 0.303 | **2,532** |
| `Int16` | 2.000 | 3.191 | **0** |
| `Int8` | 2.000 | 3.191 | **0** |

End-to-end through a real file (`UnitReferredImportStarDetectionTests`, 16-bit PNG, 40 planted
stars): **0 before, 38 after.** Nothing reported it -- the star overlay, HFD/FWHM, Boost, Calibrate
and SPCC are all gated on a non-empty star list, so they went quiet. Same symptom the enhance path
already has a note about in CLAUDE.md, different cause.

The fix is `Image.SamplesAreUnitReferred`, set by the two importers, forwarded by the five sites that
copy another image's `BitDepth`. The predicate is a union (`Float32 || flag`) and the peak tolerance
still gates both, so a sample at 255 cannot be talked into unit scale by a flag. The pre-existing
`AnIntegerImageIsNeverUnitScaledFloatHoweverSmallItsPeak` still passes: it constructs without the
flag, which is the ADU case it was written for.

### What the retry loop actually costs, and why `minStars` is not the lever

The theory was that the viewer asking `maxStars: 2000` made the detector rescan the frame up to three
times. **It does not, and the parameter caps nothing.** `maxStars` has exactly one effect: it
supplies the default for `minStars`. And `retries` is decremented *before* the loop condition is
re-tested, so `maxRetries: 2` yields at most **two** passes -- while the `detection_level <= 7 *
noise` guard usually stops it at one (it fires on two of the three real fixtures).

Per-pass, measured on the 3008x3008 RGGB fixture:

| stage | cost |
|---|---|
| `Background` (histogram + iterative noise SD) | 166-175 ms |
| detection pass 1 | 29-82 ms |
| detection pass 2 | 57 ms |

So the retry chain is the cheapest part of a detection, and the pass it precedes costs more than the
pass itself. Lowering `minStars` to 200 on that frame saves ~40 ms and costs **63% of the star list**
(3,014 -> 1,127). Not a trade worth taking; the call site keeps 2000 and the parameter doc now says
plainly that it caps nothing.

### The real cost of a document open, and a retracted measurement

**This section first reported `Statistics` as the dominant traversal at ~14 ns/px. That was a DEBUG
measurement and is withdrawn.** `dotnet test` defaults to Debug and this library's inner loops run
~7x slower there, so the number characterised the test configuration, not the product. Quote the
configuration alongside any timing in this file.

Re-measured in **Release**, 6000x4000x3 (24 MP), planes touched first so page faults are not
charged to whichever stage runs first:

| stage | Release before | Release after | scaled to 124 MP (after) |
|---|---|---|---|
| `Statistics(c)` x3 | 130-177 ms | 120-150 ms | ~0.6-0.8 s |
| `GetStarMaskedMedianAndMADScaledToUnit(c)` x3 | 412-632 ms | **23-70 ms** | ~0.1-0.4 s |
| `Background` (inside `FindStars`) | 18-56 ms | 16-56 ms | ~0.1-0.3 s |
| detection passes | 22-69 ms each | 23-65 ms each | ~0.1-0.3 s each |
| `ScanBackgroundRegion` x2 | 4-26 ms each | 4-44 ms each | negligible |

A document open still performs 10-12 full traversals, but the one that dominated was the
**star-masked median**, and for an algorithmic reason: two full `Array.Sort` calls over ~1.5 M
samples per channel to extract two medians. Selection replaced sorting
(`StatisticsHelper.NthSmallest` over the existing `QuickSelect`), bit-identically, and the pair
dropped ~7x. The histogram loop went 50 -> 32 ms per 24 MP channel via a flat span and a
float-domain clamp; a further 32 -> 8 ms is available by parallelising row bands and is filed
rather than taken, because it reorders the `total_value` summation that feeds the detection
threshold.

The wider point for this plan: **"why is opening a big TIFF slow" is a different question from
"why does it cost 2.2 GB", and this plan had assumed they were the same.** The timing question is
now largely answered and is orthogonal to the memory milestones below.

Of those traversals exactly one -- the decode -- is fused with anything. `Background`'s histogram
and its ~5,000-sample noise grid are both accumulative and could fold into the decode sink for
free; the detection scan cannot, because its threshold is a global property of the whole frame and
the first trigger comparison needs the last strip.


### The two things that will actually bite

**`IPreviewSource.GetChannelData` returns `ReadOnlySpan<float>`.** That is the API decision, and it
has four implementers (`AstroImageDocument`, `LiveFramePreviewSource`, `SerPreviewSource`,
`LiveStackPreviewSource`). Three of them are live/streaming sources whose frames are genuinely float
already, so a blanket change would cost them a conversion to save nothing. The shape that fits is a
sibling member describing the *native* samples (depth + span) with the float accessor kept for
sources that have floats natively -- not a replacement.

**FITS is the case M3 does NOT pay for, and that is worth stating up front.** A 32-bit float FITS is
already `R32Sfloat`, so there is nothing to narrow. A 16-bit integer FITS carries `BZERO`/`BSCALE` and
is signed, so its raw samples are *not* directly uploadable to `R16Unorm` without applying that
offset -- and applying it is what the float conversion currently does. So the clean win is 8-bit
display documents: TIFF, PNG, JPEG. Which is precisely the document that motivated this plan (a 5.7 MB
architectural drawing costing 2.5 GB), and precisely *not* the astronomical frames the app exists for.

That asymmetry is the honest summary of M3's value: **it makes the viewer good at documents it was
never designed for, and changes nothing for the ones it was.** Worth doing -- opening a big TIFF
should not cost 2.2 GB -- but it should be sized against that, not against the astro path.

### Recommendation

Ship **D3'** first (retain the raster + upload it directly: self-contained, no `IPreviewSource`
change, net -6 B/px), then decide D1' against measurements taken with it in place. Do not start D2
until star detection has been shown to be the thing holding the floats resident -- that is still an
assumption above, not a measurement, and the whole reason this section exists is that the last two
attempts to reason about this file's memory were both wrong until something deterministic was
measured.

**The `BitDepth` prerequisite above is done and is the order to keep**: it was found by instrumenting
the open path for D3', it inverts what "slow open" meant, and shipping D3' on top of the conflation
would have made a correctness bug harder to see rather than easier.

**Verification must be allocation- and device-memory-based, never working set.** Established the hard
way in M2: run-to-run variance on this document exceeds 1,200 MB, which is larger than anything M3
would deliver. `GC.GetAllocatedBytesForCurrentThread` for the CPU half; the pipeline's own image
sizes (the `StagingBufferSize` pattern) for the device half.

## Phasing

| Phase | Items | Rationale |
|-------|-------|-----------|
| A | **DONE** -- M1 | Self-contained, one repo, no API design. Fixes the cost that persists across documents -- the one a user experiences as "it got slow after I opened that file". |
| B | **DONE** -- M2 in `Codecs` (3.11) | A new public API on `SharpAstro.Tiff` plus a release. Second, so A has shipped by the time the pin moves. |
| C | **CODE DONE**, pin held until 3.11 publishes -- M2 in `tianwen` | Follows B's release; the codec family floats per minor, so the pin edit is one line. |
| D | **D3' DONE** (measured -6.05 B/px RGB, -2.02 mono); D1'/D2 designed -- M3 above | D3' shipped first as recommended: device-only, no API change. D1' demotes the float planes to a transient and is the next decision, to be taken against measurements with D3' in place. D2 (depth-agnostic star detection) stays gated on MEASURING that star detection is what holds them resident -- still an assumption. 16-bit retention is gated behind D1' too: the same arithmetic gives net 0 while the float planes stay resident. |

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
- **M3**: NOT a working-set comparison -- M2 established that run-to-run variance on this document
  exceeds 1,200 MB, which is larger than anything M3 delivers. `GC.GetAllocatedBytesForCurrentThread`
  for the CPU half, the pipeline's own image sizes (the `StagingBufferSize` pattern) for the device
  half. Plus a pixel-identity check that an 8-bit document renders byte-identically through the
  `R8Unorm` path and the float path. If it does not, the sampler normalisation and
  `Image.StretchValue`'s normalisation have diverged, which is a real bug and not a tolerance to
  widen.
