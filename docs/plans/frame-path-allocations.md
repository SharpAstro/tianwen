# Frame-path allocations: what a frame costs on its way through

**Status: P0 DONE (2026-09-24, merged in #349, with FITS.Lib 6.1); P1 DONE (2026-09-24); P2 to P5 NOT
STARTED.**
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
| Canon EVF (`CanonCameraDriver`, `RasterImage.ToFloats`) | about 31 B/px per frame, 21.6 MB at 1024 x 680, pacing allows 66 fps | `RasterImage.ExpandToFloats(Span)` into a rented buffer, and a plane free list |
| Fake video renderers (`JupiterTextureRenderer`, `SyntheticPlanetRenderer`) | 11 planes per colour frame, no `ChannelBuffer` | the renderers already take `dest:`; pass it |
| Small but per frame | a `TaskCompletionSource` per frame; `RollingWindowStacker._scoreCache` gains an entry per frame and is never trimmed (FIXED: it keeps the window and one window before it, 60 frames seen through an 8-frame window hold 16 entries, not 60); Hann `double[T]` x 2 and an FFT column per aligner call (FIXED with the aligner row above) | fields, a bounded cache |

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
| PHD2 capture source (`GuiderCaptureSource`): `TryReadFitsFile` UNPOOLED per frame, plus FITS.Lib's typed array and the reader's two 2 MB buffers | about 17 MB per 2 MP frame | `pooled: true` (the image is already released); a 64 KiB read buffer; better, the mapped reader in P5 |
| ASTAP fallback when the catalogue solve fails | a temporary FITS per solve | acceptable: fallback only |
| Canon stills: a temporary CR2 round trip, `CanonRaw.Open`, `PreprocessMosaic` (a flat float[]) and a new plane, none recycled | about 300 MB per sub | the DAL-style free list plus `ChannelBuffer` |
| ASCOM out-of-process host (`AscomHostProcess`, `RemoteDispatchTransport`): a line string, a `JsonDocument` and an `int[,]` | about 40 B/px per frame | only for drivers that cannot run in-process under CET; a binary frame transfer if it ever matters |
| Fake camera partial-cloud simulation (`SyntheticStarFieldRenderer`) | a 12 B/px map per frame | test-only; rent it if it ever shows |

Confirmed clean by the same sweep: `LiveFramePreviewSource.AcceptFrame` allocates only on a geometry
change; `VkFitsImagePipeline`'s staging buffer is persistent and grow-only; the built-in guider's DAL
frames alternate between two recycled buffers.

## P5: reading a FITS file (the viewer, the stacker, and re-hydrating in the hardware split)

**`TryReadFitsFile` allocates three times the frame to read it**:
- FITS.Lib's typed array;
- the float plane;
- two 2 MB buffers, since the read side still asks `BufferedFile` for `1000 * 2088`.

**A memory-mapped read that goes straight from the file's big-endian samples to the float plane
allocates nothing, and is twice as fast.** It finds END, then byte-swaps, adds BZERO and widens in one
vector pass. Measured on a 26 MP 16-bit sub, hot page cache, win-arm64, Release:

| Read | Time | Allocated |
|---|---|---|
| `TryReadFitsFile` | 44 ms | 158.7 MB |
| `TryReadFitsFile`, pooled | 40 ms | 54.3 MB |
| mapped, one pass into a recycled `float[,]` | 22.6 ms | 0 |
| mapped, straight to uint16 (what a 16-bit texture wants) | about 20 ms | 0 |

- **It belongs in FITS.Lib**, as the reading counterpart of `FitsWriter`. It serves the viewer, the
  stacker (hundreds of subs per integration), the PHD2 capture source and, in the hardware split, the
  GUI re-hydrating a sub the server saved.
- **Saved frames need no slot at all.** The file IS the shared memory: page cache, reclaimable, at the
  sensor's own 16 bits. The plan's shared-memory slots remain only for frames that are never saved
  (previews, polar refinement, guide frames, planetary video), and for planetary the SER ring above
  may take that role too.
- **Plain FITS only.** A `.gz` or `.fz` file must be decompressed, so it cannot be mapped.

## Side findings

- **`Array2DPool.Rent`'s documentation says "zero-cleared", but the code does not clear.** Every caller
  added here overwrites every element, so it is safe; the comment is wrong and any caller that relies
  on it is not.
- **Each forced gen2 also ran `Array2DPool`'s trim.** Above 70% memory load that drops entries older
  than 30 s, which at a 60-300 s sub cadence discarded the pooled planes on every sub. With the forced
  collection gone, the trim runs only on natural gen2s.
- **FITS.Lib's `Header.Simple` stamps the time of writing into the `SIMPLE` card's comment**, so no two
  writes of the same image are the same file. Harmless, but it defeats byte comparison and dedup; a
  deterministic comment would restore both.
