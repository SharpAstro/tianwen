# Frame-path allocations: what a frame costs on its way through

**Status: P0 DONE (2026-09-24, merged in #349, with FITS.Lib 6.1); P1 DONE (2026-09-24, #350); P2 to P4
DONE (2026-09-24, #365): what is left is the SER frame ring (P2), the sibling libraries' own buffers, and
three rows kept on purpose; P5 DONE (2026-09-25, #755, with FITS.Lib 6.2).**
Raised by the user on 2026-09-24: "if we have allocations that are useless already right now, we should
remove them prior to this server change" (the hardware-in-the-server plan, not yet on `main`). A
read-only sweep of every capture path the same day found the items below; each carries the file, the
size, the rate and the cheapest fix it named. Rates assume a 26 MP main-camera sub every 60 to 300 s, a
2 MP guide frame every 1 to 2 s and a 640 x 480 planetary ROI at 60 to 200 fps. Line numbers drift;
member names do not, so they are the citation.

**The rule behind every item: an allocation the size of the frame, made per frame and dropped at once,
is garbage the GC must sweep, and on the large-object heap it waits for a gen2.** The session used to
force one after every FITS write for exactly that reason (P0).

## P0: the per-sub path (DONE)

| Item | Before | After | Commit |
|---|---|---|---|
| DAL download (`DALCameraDriver.DownloadImage`) read 16-bit pixels as SIGNED | 34.5 ms, a new 52 MB `short[]` per 26 MP frame, and every pixel of 32768 or more negative | 3.5 ms, no allocation, unsigned (`RawPixelConversion.WidenToSingle`, one fused vector pass) | "fix(dal): a 16-bit camera's pixels are read unsigned, in place, in one pass" |
| FITS write quantised into `new T[h, w]` | 52 MB garbage per 16-bit sub | streamed: quantised a 2 MB band at a time, swapped in place (FITS.Lib 6.1 `FitsWriter`) | "perf(fits): write through FITS.Lib's FitsWriter, a band at a time, gzip straight through" |
| FITS write buffers (`BufferedFile` given 2 MB, which it hands to BOTH a `FileStream` and a `BufferedStream`, plus FITS.Lib's 2 MB swap chunk) | 6.3 MB garbage per write | none: the writer is unbuffered and rents its band | same, plus FITS.Lib "perf(write): the endian-swap chunk is rented, and no larger than the image" |
| `.fits.gz` sidecars written to a scratch file, then compressed | a second full write and read of the file | straight down a `GZipStream` (the old writer built a `BinaryReader` over any stream and refused a write-only one) | "perf(fits): write through ..." |
| Star detection on a colour mosaic debayered into a new plane | 104 MB garbage per 26 MP colour sub | rented (`DebayerIntoAsync` into an `Array2DPool` lease) | "perf(imaging): a captured sub allocates no frame for its FITS write or its star detection" |
| `GC.Collect(2, Forced, blocking)` + `WaitForPendingFinalizers` after every FITS write | every thread suspended once per sub (29 ms on a 643 MB synthetic heap) | removed; 20 simulated 26 MP colour subs: 302 -> 335 MB with ONE natural gen2 | "perf(session): no forced full GC after every FITS write" |

Measured, 26 MP, win-arm64, Release: a 16-bit sub write went from 74 ms and 56.4 MB allocated to 70 ms
and 0.07 MB; a float master from 4.25 MB to 0.07 MB (45 ms); a gzipped 16-bit map from 1.30 s and 58.6 MB
to 1.12 s and 0.14 MB. Nine golden files (8/16/32-bit integer and float, mono, mosaic and RGB, scaled
maps, extra cards, gzip) are byte-identical before and after, apart from the time FITS.Lib's `SIMPLE`
card has always stamped into its comment. The functional suite's peak working set moved only about 2%
(719-723 MB to 693-709 MB over three runs each): its frames are small, and the savings scale with the frame.

**What the signed read did to frames already written: every pixel of 32768 or more is a 0 on disk.**
Read from the code (2026-09-24), not yet measured on a file, because the archive is not on the machine
this was found on:
- The camera frame reached the writer with those pixels negative, and the writer stores a 16-bit frame
  conventionally (`FitsSampleStorage.Conventional`, `BZERO` 32768), which clamps anything below the
  container's bottom to stored -32768: physical 0. The writer on `main` on 2026-09-22 already went
  through `FitsSampleStorage`, so this holds for every frame TianWen has captured through a DAL camera.
- On the left-aligned 12-bit data Player One and ToupTek hand over, 32768 is HALF the converter's
  range, so a hot pixel in a dark or a star core in a light need not saturate to be lost.
- **The frames known to be affected are the 253 group P bias and darks** shot with `tianwen darks` on
  2026-09-22 for the Uranus-C Lagoon-and-Trifid sessions (#307 `#78`, `#94`), which the re-bake uses.
  Any hot pixel above half scale in those darks reads 0. The bad-pixel detector sees a 0 as a cold
  outlier, so the mask probably excludes those photosites anyway, but their values are gone.
- **The signature to check**: exact zeros in a frame whose pedestal sits well above zero, which a sound
  frame does not have, together with no pixel at or above 32768. A maximum below 32768 alone proves
  nothing, since a short dark may never reach half scale.

## P1: the TUI live preview corrupted the sub it previewed (CORRECTNESS, FIXED 2026-09-24)

`TuiLiveSessionTab.RenderPreview` passes `LiveState.LastCapturedImages[i]`, which is the session's OWN
`Image`, to `AstroImageDocument.AdoptImageAsync`. That method CONSUMES its input by its own contract: it
rescales the pixels to [0, 1] in place (`ScaleFloatValuesToUnitInPlace`).
- The session queues that same `Image` for its FITS write (`Session.Imaging`, the image write queue),
  and the queue drains only on a later tick.
- The TUI sees the frame within a render of it appearing, so the rescale very likely lands first.
- The sub is then written as [0, 1] data quantised to 0 or 1 ADU: destroyed.
- The buffer is also the camera's recycled one, so the document can later read pixels that belong to the
  next exposure.

Read from the code (2026-09-24), not reproduced. The GUI is not affected: its `LiveFramePreviewSource.AcceptFrame`
copies. The session also runs star detection on that same `Image` while it waits in the write queue, so the
rescale raced the star count as well as the write.

**Fixed:** `AstroImageDocument.FromLiveFrameAsync` leases the frame (`Image.TryLease`), copies it
(`Image.Clone`), disposes the lease and adopts the COPY, and the TUI previews through it. A frame already
given back returns `null` (the next frame shows instead). `LiveFrameDocumentTests` pins it, written first:
against the old direct adoption, the frame's pixels changed and a released frame threw; a third test holds
that the lease is given back, so the owner's release still recycles the buffer.

## P2: video rate (planetary), the largest by far

| Finding | Size and rate | Fix |
|---|---|---|
| Live-stack master build (`LiveStackPreviewSource`, `WaveletSharpen`, `ATrousWaveletTransform`, `WaveletDecomposition`): 11 planes per channel even with sharpening OFF, since off runs a 6-scale identity pass; plus the master planes, a colour frame's merge and MHC demosaic, and `AdoptImageAsync`'s luma plane, histograms and three `BitMatrix` | about 16 MB per master (mono), 48 MB (colour); 80-480 MB/s at 5-10 masters/s | **Wavelet pass FIXED**: identity gains are recognised and copied (clamped), which is also EXACT where the decomposition matched the input only to float rounding; a real sharpen rents its layers from `Array2DPool` (the output doubles as the convolution scratch, so six scales rent eight planes, the pool's per-shape cap) and reconstructs in the reference order, bit for bit `Decompose` + `Reconstruct`. Measured per 256 x 256 channel: sharpening off 2.9 MB to 262,896 bytes, `PlanetaryDefault` 2,913,872 to 291,816 (the output plane plus the parallel loops' closures). The merge plane of a colour master is **FIXED** too (rented, `MergeBayerChannelsInto`): 4,199,880 bytes to 3,150,760 from 256 x 256 sub-planes, the three colour planes that ARE the master; the batch Bayer drizzle (`LuckyImagingStacker.StackDrizzleAsync`, not in the sweep) merged a new mosaic per FRAME and now reuses one per stack. The document's luma plane is **FIXED** too (rented), and so is the histogram every median-and-MAD statistic built and dropped (rented bins, one traversal shared with `Histogram`): a colour document's luma statistic 1,314,400 bytes to 1,024 over 512 x 512, a median and MAD 0 |
| `LiveCameraFrameStream.Push` deep-copies every frame | a new plane per channel per frame, 1.23 MB (mono or split colour) or 3.7 MB (RGB): 74-246 MB/s; the ring retains `max(2 x 500, 1024)` copies, about 1.26 GB at 640 x 480 | **FIXED**: the ring's planes are `ChannelBuffer`s on a free list and `LoadAsync` hands out leases (every consumer already released what it loaded, in a `finally`), so a frame a stacker still holds keeps its pixels after its slot is overwritten. Measured, 100 pushes of a 256 x 256 frame into a 4-frame ring: 26,289,600 bytes to 79,200 (792 per push, the `Image` itself), and the ring holds capacity + 1 planes. The SER ring below remains the next step |
| `SplitBayerChannels` per colour frame (`PlanetaryCaptureController`) | 1.23 MB per frame, 74-246 MB/s; the later `Release()` does nothing because the split image owns its arrays | **FIXED**: the capture loop pushes the mosaic WHOLE and the ring splits it straight into its recycled planes, normalising in the same pass, bit for bit what split-then-push gave (pinned for all four Bayer phases). 50 pushes of a 256 x 256 mosaic allocate less than one sub-plane |
| `GlobalAligner.Estimate` per added frame (`PhaseCorrelation`) | a float tile and a complex spectrum of T^2 each: 1.3 MB at T = 256, 5.2 MB at T = 512, and every rebuild re-aligns the whole window: 79-262 MB/s at T = 256 | **FIXED**: tile and spectrum are per-instance scratch (one `Estimate` at a time per aligner), the Hann windows are cached per length, `Fft2D`'s column scratch is on the stack. Measured at T = 256: 1,323,152 bytes per frame to 0 |
| `AlignmentPointMatcher.BuildMesh`, the batch lucky-imaging path (not in the sweep; found fixing the row above) | per alignment point per frame: the FIXED reference patch re-transformed (dead work, one FFT of three) plus two new spectra; 1,142,560 bytes per mesh at 32 points and a 32 px patch | **FIXED**: each reference spectrum is computed once in `FromReference` (numerically identical, pinned by `Precomputed_reference_spectrum_matches_single_call_exactly`), frame patch and spectrum are per-instance scratch. Measured: 1,032 bytes per mesh, the mesh itself |
| Canon EVF (`CanonCameraDriver`, `RasterImage.ToFloats`) | about 31 B/px per frame, 21.6 MB at 1024 x 680, pacing allows 66 fps | **FIXED**: `TryDecodeRaster` widens the decoded samples straight into the planes (`WidenDecodedInto`, exactly `ExpandToFloats`'s values, pinned for every sample format and channel count), so no decode anywhere makes the 16 B/px RGBA float copy; and the EVF decodes into a `PlaneRecycler`, the drivers' hand-rolled free list for any number of planes, whose frames hand their planes back on release (bit-identical to the self-owned decode after the recycler was handed NaN planes). Measured: a decode 32.0 to 16.0 B/px; a steady recycled EVF frame 4.3 B/px, about 3.0 MB at 1024 x 680 where it was 22.3. Left: the decoder's own 8-bit raster and FC.SDK's per-frame JPEG `byte[]`, both in sibling libraries |
| Fake video renderers (`JupiterTextureRenderer`, `SyntheticPlanetRenderer`) | 11 planes per colour frame, no `ChannelBuffer` | **FIXED**: the renderers' working planes are rented from `Array2DPool` (cleared where the sky is what stays 0), the blur runs in place with one scratch plane (`GaussianBlurInto`: the horizontal pass has read all of the source before the vertical pass writes), the mosaic is written straight into the output and noised in place, and `FakeCameraDriver` renders each video frame into a `PlaneRecycler` plane its release hands back. Pinned bit-identical to a render into fresh arrays with NaN in the output and in the pool, for all three renderers. Measured, a 320 x 240 colour render into its output: 3,073,040 bytes to 304; a released stream renders every frame into one plane |
| Small but per frame | a `TaskCompletionSource` per frame (KEPT: the capture loop's frame signal is re-armed per frame so a waiter gets a task of its own, about 100 bytes, and pooling it would hand an old waiter the next frame's completion); `RollingWindowStacker._scoreCache` gains an entry per frame and is never trimmed (FIXED: it keeps the window and one window before it, 60 frames seen through an 8-frame window hold 16 entries, not 60); Hann `double[T]` x 2 and an FFT column per aligner call (FIXED with the aligner row above) | fields, a bounded cache |

**A memory-mapped SER as the frame ring (the user's suggestion, 2026-09-24).** A SER file is a fixed-size
frame store: a 178-byte header, raw little-endian 8- or 16-bit frames, a timestamp trailer.
- **What it would replace.** The capture loop writes each frame ONCE, in its native format (Bayer
  mosaic, uint16). The stacker and, after the hardware split, the GUI read frames from the same mapped
  pages. That replaces the 1.26 GB ring of float copies with reclaimable page cache at half the size,
  and removes the per-frame deep copy and the split as allocations.
- **Where it lives.** A temporary file when not recording (`FILE_ATTRIBUTE_TEMPORARY`, deleted on
  close, which the cache manager keeps in memory when it can); the recording itself when recording.
- **SER.Lib is half there.** `SerReader` is already memory-mapped. `SerWriter` is not: it opens with
  `FileShare.None` and writes the header, frame count included, only at close. A live SER needs:
  - a mapped writer with a preallocated capacity;
  - the frame count published as frames land (the reader's seqlock);
  - `FileShare.Read`;
  - a reader that re-reads the count, and a new file when the ROI changes the frame size.

## P3: guide rate (1 to 2 s, 2 MP)

| Finding | Size and rate | Fix |
|---|---|---|
| ASCOM download (`SafeArrayMarshal.ToInt32Array2D`): a flat `int[]` and then an `int[W, H]` before the transpose into the recycled float plane | 8 B/px: 17 MB per guide frame (8.5-17 MB/s), 209 MB per 26 MP sub | **FIXED**: the SAFEARRAY is column-major [W, H], which is already row-major H x W, so `SafeArrayMarshal.ToImageChannel` widens it in one vector pass (`RawPixelConversion`, signed `int` and `short`) straight into the recycled plane, with `FromWxHImageData`'s result, range and zero-floored maximum exactly (pinned against the old path, VT_I4 and VT_I2). Measured at 640 x 480 into a recycled plane: 2,457,664 bytes per frame to 0. In-proc COM only; the out-of-process host keeps its row below |
| Guider tracker acquisition (`GuiderCentroidTracker`): the `peaks` list collects every 4-neighbour local maximum with no threshold, roughly 15-20% of pixels | about 6.3 MB after 12.6 MB of doubling, per acquisition frame and every frame while the star is lost: 6-12 MB/s during star loss | **FIXED**: `peaks` and `candidates` are per-tracker scratch, cleared per acquisition (a tracker takes one frame at a time). Measured at 640 x 480: 2,098,008 bytes per acquisition to 344, the two star profiles the result hands back. The sort of every peak and a centroid per peak during star loss remain, as CPU rather than garbage |
| Alpaca payload (`AlpacaClient`, `ReadAsByteArrayAsync`) | the whole payload per frame: 4-8.5 MB per guide frame, 52-104 MB per sub (the decode itself is recycled) | **FIXED**: the response is read from its headers and the body streamed straight into a buffer rented at Content-Length (grown, for a chunked body), handed out as a disposable `ImageBytesPayload` the driver disposes after the decode. HttpClient's timeout stops at the headers once a body is streamed, so the same budget covers the body, and a stall fails as HttpClient's own timeout does (pinned against a body that never arrives, seen to hang without it). Measured on a 1.2 MB payload: 2,460,480 bytes per download to 2,936, and HttpClient's buffer plus the copy out were both payload-sized |
| GUI live preview histograms (`StretchSolver`, `Image.Histogram`, `HistogramDisplay`): `uint[MaxValue + 1]` per frame, 2 (mono) or 6 (mosaic), and `MaxValue` is the frame's peak so the bin count changes almost every frame and `HistogramDisplay` is rebuilt | 0.75 MB (mono) or 2.3 MB (mosaic) of LOH per frame | Stats-only half **FIXED**: `GetPedestralMedianAndMADScaledToUnit` runs the histogram traversal over rented bins (0 bytes per call; `StretchSolver.CollectPerChannelStats` makes one per channel per frame). Display half **FIXED**: the renderer takes a histogram only when the overlay DRAWS it (both live hosts start with it hidden), and `LiveFramePreviewSource` takes its display histograms on first read, over its normalised planes at a fixed 65536 bins, so an exposure with the overlay hidden computes none: 240,208 bytes per 60000-ADU exposure to 72. Shown, `HistogramDisplay.Refresh` takes new statistics in place into a grow-only buffer (0 bytes within the widest bin count so far), where a bin count moving with the peak rebuilt it every exposure. The bins an open overlay needs are still a new array per exposure, since an `ImageHistogram` is immutable |
| Hosted guider preview (`GuidePreview`, `PreviewEncoder`): a colour guide camera is MHC-debayered on every new frame for every remote client | 12 B/px: 25 MB per guide frame | **FIXED**: a mosaic is debayered with `DebayerIntoAsync` into three planes rented from `Array2DPool`, returned once the encode has read them; MHC writes every destination pixel, pinned by a JPEG byte-identical to the fresh debayer's with the pool first handed NaN planes of that shape. Measured on a 512 x 384 mosaic at scale 0.5, fewest bytes of five calls: 2,732,368 to 373,624, the JPEG's own input and output. Every preview endpoint shares the encoder, so the per-OTA and ninaAPI previews gain the same |

## P4: polar alignment (about 1 Hz, 26 MP) and the remaining per-sub paths

| Finding | Size and rate | Fix |
|---|---|---|
| Polar refine `Downsample` per frame (`IncrementalSolver.RefineAsync`, `Image.Transform`): a new float[h/f, w/f] per channel below 1.5"/px; full-solve iterations downsample twice more | 26 MB/s at f = 2 (61 MB/s on an IMX455) | **FIXED**: `Image.DownsampleRented` bins into planes from `Array2DPool` behind a `RentedImage` that returns them once (a class, so a copy cannot return them twice), and `IncrementalSolver` (seed and every refine) and `CatalogPlateSolver` return them as soon as the detection has read them; `Downsample` and the rented form share one loop, pinned bit-identical (a NaN block and the binned metadata included) after the pool was handed junk planes. Measured at 1024 x 768, factor 2: 787,184 bytes to 744. The incremental path had no active test (its seven target the retired centroid matcher, and at 1.55"/px would never bin); one now seeds and refines a 2x-binned frame to the seed's own solution. The seed's `SortedStarList` is now disposed when its quad build is cancelled |
| PHD2 capture source (`GuiderCaptureSource`): `TryReadFitsFile` UNPOOLED per frame, plus FITS.Lib's typed array and the reader's two 2 MB buffers | about 17 MB per 2 MP frame | **FIXED** in part: the refine loop's read is `pooled: true` (it already released the image after its solves), and `CaptureAndSolveAsync`, whose solve reads the FILE, no longer reads the frame at all: 8,953,224 bytes per 1024 x 768 capture and solve to 13,256. P5 took the rest: the pooled read goes through FITS.Lib's `FitsReader` now, and pays neither FITS.Lib's typed array nor `OpenFits`'s 2 MB frame read-ahead, which every frame read in the app shared (a header-only read has had its own 64 KiB opener, `OpenFitsHeader`, since "perf(fits): a header-only read fills a header-sized buffer, not a frame-sized one") |
| ASTAP fallback when the catalogue solve fails | a temporary FITS per solve | acceptable: fallback only |
| Canon stills: a temporary CR2 round trip, `CanonRaw.Open`, `PreprocessMosaic` (a flat float[]) and a new plane, none recycled | about 300 MB per sub | **FIXED** in part, TianWen's share: the driver reads each sub into a `PlaneRecycler` plane (`Image.TryReadCanonRaw` with a plane provider) and hands it on with the recycling `ChannelBuffer`, the DAL pattern. Measured on the 20 MP CR2 fixture: 265,297,112 bytes per read to 185,443,320, the difference exactly the plane. **Left, in FC.SDK.Raw** (not auto-detected, so a NuGet release): the decode's own buffers and `PreprocessMosaic`'s flat `float[]` over the whole raster (together the 185 MB), and opening from the downloaded bytes instead of a temporary file |
| ASCOM out-of-process host (`AscomHostProcess`, `RemoteDispatchTransport`): a line string, a `JsonDocument` and an `int[,]` | about 40 B/px per frame | only for drivers that cannot run in-process under CET; a binary frame transfer if it ever matters |
| Fake camera partial-cloud simulation (`SyntheticStarFieldRenderer`) | a 12 B/px map per frame | test-only; rent it if it ever shows |

Confirmed clean by the same sweep: `LiveFramePreviewSource.AcceptFrame` allocates only on a geometry
change; `VkFitsImagePipeline`'s staging buffer is persistent and grow-only; the built-in guider's DAL
frames alternate between two recycled buffers.

## P5: reading a FITS file (the viewer, the stacker, and re-hydrating in the hardware split) (DONE 2026-09-25, #755)

**`TryReadFitsFile` allocated three times the frame to read it**:
- FITS.Lib's typed array;
- the float plane;
- two 2 MB buffers, since the read side still asked `BufferedFile` for `1000 * 2088`.

**A plain file is now read by FITS.Lib 6.2's `FitsReader`, straight from the file into the float
planes** (`Image.TryReadThroughFitsReader`); a gzipped or tile-compressed file, and anything the reader
declines, still goes through the HDU reader. The reader walks to the same HDU, parses its header with
FITS.Lib's own parser, and reads each plane a 2 MB band at a time, swapping, widening and scaling each
band into the plane: no typed array and no read-ahead buffer. It serves the viewer, the stacker
(hundreds of subs per integration), the PHD2 capture source and, in the hardware split, the GUI
re-hydrating a sub the server saved.

**Positional reads, not the memory mapping this section first proposed.** The mapping measured 22.6 ms
hot on 2026-09-24, and it was never measured cold. Both measured on 2026-09-25 at the FITS.Lib level,
one 26 MP BITPIX 16 sub, win-arm64, Release, tiered compilation off, hot from the page cache (median of
15) and cold (written with unbuffered I/O, so no page of it was cached; median of 4):

| Read | Hot | Allocated | Cold |
|---|---|---|---|
| FITS.Lib's HDU reader, converted as `TryReadFitsFile` did | 81.8 ms | 158.7 MB | 100.5 ms |
| the same into a preallocated plane | 46.1 ms | 54.3 MB | 90.4 ms |
| memory-mapped (`PartialFitsReader` over the whole frame) | 24.6 ms | 3.4 KB | **105.6 ms** |
| memory-mapped, `PrefetchVirtualMemory` first | 27.4 ms | 0.8 KB | 70.8 ms |
| **2 MB positional reads into a rented band** | **10.0 ms** | 0.5 KB | **65.7 ms** |

- **A mapping loses both ways.** It pays a page fault for every page on every read, and cold it faults
  the file in a few pages at a time, so cold it was SLOWER than the reader it was meant to replace,
  which is the stacker reading a night off a USB disk. A 2 MB read is one request.
- **Saved frames still need no slot.** The bytes come out of the page cache every process shares,
  reclaimable and at the sensor's own 16 bits, whether they are mapped or read. The plan's
  shared-memory slots remain only for frames that are never saved (previews, polar refinement, guide
  frames, planetary video), and for planetary the SER ring above may take that role too.
- **`PartialFitsReader` keeps its mapping**, which suits its many small tile regions of one frame; it
  decodes through the same FITS.Lib decoder (`BigEndianSamples`) now, so a region and its plane agree.

**Through `Image.TryReadFitsFile`**, the same sub and conditions, hot median of 15, cold median of 3:

| Read | Hot, a sub TianWen wrote | Hot, another program's (no range cards) | Allocated | Cold, pooled |
|---|---|---|---|---|
| HDU reader | 73.0 ms | 72.9 ms | 158.7 MB | |
| HDU reader, pooled | 39.5 ms | 52.4 ms | 54.3 MB | 75.1 / 105.5 ms |
| `FitsReader` | 24.3 ms | 17.8 ms | 104.4 MB, the plane itself | |
| **`FitsReader`, pooled** | **10.5 ms** | **14.3 ms** | **18 to 56 KB** | **61.8 / 73.4 ms** |

Unpooled and cold, the two were the same within the noise of three reads (71 to 106 ms against 72 to
109). The other program's sub carries no DATAMIN or DATAMAX, so both paths also scan the planes for the
range. `FramePathAllocationTests.APooledSixteenBitFitsReadAllocatesNoFrameSizedArray` pins it: a pooled
6 MP read allocated 14,124,256 bytes before, against a bound of half the typed array.

- **The same image, bit for bit.** The reader converts a sample as `ConvertChannel` does, `bscale *
  stored + bzero` in single precision, multiplied then added, and copies a float plane with no scaling
  unchanged, which is what adopting FITS.Lib's own float array amounted to. `FitsReadPathParityTests`
  reads 33 files both ways, pooled and not: every BITPIX with no scaling, unsigned and scaled, mono and
  a cube; TianWen's own files, one with a WCS; float specials (payload NaNs, -0.0, infinities,
  subnormals); an empty primary; a table before the image. BITPIX 64 and -64 both refuse. Its reader
  side is `TryReadThroughFitsReader` itself, never the public entry, whose fallback would compare the
  HDU reader with itself for any file the reader quietly declined.
- **Found doing it: the first image must hold a SAMPLE.** FITS.Lib writes a placeholder in front of a
  table (`BasicHDU.DummyHDU`, the image of an empty array: NAXIS = 1, NAXIS1 = 0). It is an image HDU,
  and the walk (`FitsHduExtensions`) stopped there, so such a file read as unreadable while the
  header-only read described the placeholder. The walk and the reader now both skip an image HDU with
  an axis of length zero.
- **Plain FITS only.** A `.gz` or `.fz` file must be decompressed, and goes through the HDU reader.

## Side findings

- **`Array2DPool.Rent`'s documentation says "zero-cleared", but the code does not clear.** Every caller
  added here overwrites every element, so it is safe; the comment is wrong and any caller that relies
  on it is not. FIXED: the comments now say a pooled array holds whatever its last user left in it
  ("refactor(imaging): the pool's policy is an instance the shared pool delegates to").
- **A full byte budget turned a new shape away for as long as it stayed full.** The trim runs only
  above 70% memory load, so on a roomy machine the budget one workload filled stayed filled, and every
  return of the next workload's shape was refused: a live capture after a session's masters paid a new
  plane per frame. Found on #759's CI, where the frame-path allocation tests met a budget earlier tests
  had filled; a dev box at 86% load, where the trim kept emptying the pool, passed them. FIXED: a return
  that would pass the budget evicts the arrays of other shapes returned longest ago. Measured on a 1 MiB
  pool of its own, a new shape's steady state went from 81,960 bytes per frame (every rent a new plane)
  to 0.
- **Each forced gen2 also ran `Array2DPool`'s trim.** Above 70% memory load that drops entries older
  than 30 s, which at a 60-300 s sub cadence discarded the pooled planes on every sub. With the forced
  collection gone, the trim runs only on natural gen2s.
- **FITS.Lib's `Header.Simple` stamps the time of writing into the `SIMPLE` card's comment**, so no two
  writes of the same image are the same file. Harmless, but it defeats byte comparison and dedup; a
  deterministic comment would restore both.
