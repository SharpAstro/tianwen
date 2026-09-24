# Frame-path allocations: what a frame costs on its way through

**Status: P0 DONE (2026-09-24, branch `fix/frame-path-allocations`, not yet merged); P1 to P5 NOT STARTED.**
Raised by the user on 2026-09-24: "if we have allocations that are useless already right now, we should
remove them prior to this server change" ([hardware-in-the-server.md](hardware-in-the-server.md)). A
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

## P1: the TUI live preview corrupts the sub it previews (CORRECTNESS, fix first)

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
copies. **Fix:** lease the frame (`Image.TryLease`), copy it, adopt the copy. **Test first:** the session
frame's pixels must be unchanged after the TUI previews it.

## P2: video rate (planetary), the largest by far

| Finding | Size and rate | Fix |
|---|---|---|
| Live-stack master build (`LiveStackPreviewSource`, `WaveletSharpen`, `ATrousWaveletTransform`, `WaveletDecomposition`): 11 planes per channel even with sharpening OFF, since off runs a 6-scale identity pass; plus the master planes, a colour frame's merge and MHC demosaic, and `AdoptImageAsync`'s luma plane, histograms and three `BitMatrix` | about 16 MB per master (mono), 48 MB (colour); 80-480 MB/s at 5-10 masters/s | rent the decompose/reconstruct scratch; with sharpening off, clone and clamp instead of the identity pass (may differ in the last bit); rent the luma plane |
| `LiveCameraFrameStream.Push` deep-copies every frame | a new plane per channel per frame, 1.23 MB (mono or split colour) or 3.7 MB (RGB): 74-246 MB/s; the ring retains `max(2 x 500, 1024)` copies, about 1.26 GB at 640 x 480 | recycle the evicted slot's planes through a `ChannelBuffer` free list and hand out leases (the stacker already releases what it loads); or the SER ring below |
| `SplitBayerChannels` per colour frame (`PlanetaryCaptureController`) | 1.23 MB per frame, 74-246 MB/s; the later `Release()` does nothing because the split image owns its arrays | split straight into the ring's destination, or rent and wrap in a `ChannelBuffer` |
| `GlobalAligner.Estimate` per added frame (`PhaseCorrelation`) | a float tile and a complex spectrum of T^2 each: 1.3 MB at T = 256, 5.2 MB at T = 512, and every rebuild re-aligns the whole window: 79-262 MB/s at T = 256 | keep tile and spectrum as aligner scratch (single writer); cache the Hann windows |
| Canon EVF (`CanonCameraDriver`, `RasterImage.ToFloats`) | about 31 B/px per frame, 21.6 MB at 1024 x 680, pacing allows 66 fps | `RasterImage.ExpandToFloats(Span)` into a rented buffer, and a plane free list |
| Fake video renderers (`JupiterTextureRenderer`, `SyntheticPlanetRenderer`) | 11 planes per colour frame, no `ChannelBuffer` | the renderers already take `dest:`; pass it |
| Small but per frame | a `TaskCompletionSource` per frame; `RollingWindowStacker._scoreCache` gains an entry per frame and is never trimmed; Hann `double[T]` x 2 and an FFT column per aligner call | fields, a bounded cache |

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
| ASCOM download (`SafeArrayMarshal.ToInt32Array2D`): a flat `int[]` and then an `int[W, H]` before the transpose into the recycled float plane | 8 B/px: 17 MB per guide frame (8.5-17 MB/s), 209 MB per 26 MP sub | the SAFEARRAY is column-major [W, H], which is already row-major H x W: copy it in one pass into the recycled target of `FromWxHImageData` |
| Guider tracker acquisition (`GuiderCentroidTracker`): the `peaks` list collects every 4-neighbour local maximum with no threshold, roughly 15-20% of pixels | about 6.3 MB after 12.6 MB of doubling, per acquisition frame and every frame while the star is lost: 6-12 MB/s during star loss | keep `peaks` and `candidates` as fields and `Clear()` them |
| Alpaca payload (`AlpacaClient`, `ReadAsByteArrayAsync`) | the whole payload per frame: 4-8.5 MB per guide frame, 52-104 MB per sub (the decode itself is recycled) | rent Content-Length bytes and `ReadExactlyAsync` into them |
| GUI live preview histograms (`StretchSolver`, `Image.Histogram`, `HistogramDisplay`): `uint[MaxValue + 1]` per frame, 2 (mono) or 6 (mosaic), and `MaxValue` is the frame's peak so the bin count changes almost every frame and `HistogramDisplay` is rebuilt | 0.75 MB (mono) or 2.3 MB (mosaic) of LOH per frame | take the stats-only histogram from `ArrayPool` (only median and MAD survive); let `HistogramDisplay` reuse a maximum-capacity buffer |
| Hosted guider preview (`GuidePreview`, `PreviewEncoder`): a colour guide camera is MHC-debayered on every new frame for every remote client | 12 B/px: 25 MB per guide frame | `DebayerIntoAsync` into rented planes |

## P4: polar alignment (about 1 Hz, 26 MP) and the remaining per-sub paths

| Finding | Size and rate | Fix |
|---|---|---|
| Polar refine `Downsample` per frame (`IncrementalSolver.RefineAsync`, `Image.Transform`): a new float[h/f, w/f] per channel below 1.5"/px; full-solve iterations downsample twice more | 26 MB/s at f = 2 (61 MB/s on an IMX455) | downsample into a rented plane and return it after `FindStarsAsync` (neither star list keeps it) |
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
