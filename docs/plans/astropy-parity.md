# Astropy Parity Tracker (`TianWen.Lib`)

Astropy is the reference general-purpose Python astronomy library; `TianWen.Lib` (published
standalone to NuGet, its own `AssemblyVersion`) is the part of this repo that could aspire to the
same kind of reusable, science-grade utility, as opposed to the app layers above it
(`Devices`, `Sequencing`, the UI projects) which are TianWen-specific and have no Astropy
analogue. **This file is an INDEX, not a duplicate** -- each row names the concrete `.cs` file(s)
that stand in for (or are missing from) the matching Astropy subpackage. When something here
changes, flip the row; don't let this drift from the source.

Snapshot taken 2026-08-29 against the codebase at `ca107495`. No prior document framed
`TianWen.Lib` this way -- confirmed by grep, there is zero mention of "reusable library" or
"public API surface" intent anywhere in `docs/`, so this is the first pass, not a refresh.

## By Astropy subpackage

| Astropy area | Status | TianWen.Lib evidence |
|---|---|---|
| `astropy.time` (scale conversions: UTC/TAI/TT/UT1, leap seconds, Earth rotation) | DONE, as a backend | `Astrometry/SOFA/SofaFunctions.cs` (1813 lines) ports ERFA-equivalent `Utctai`/`Taitt`/`Tttai`/`Taiutc`/`Taiut1`/`Utcut1`, `Cal2jd`/`Jd2cal`/`Dtf2d`, `Dat` + `LeapSecondsTable.cs`, `Era00`/`Gmst06`. No public `Time` value type is exposed, though -- `Astrometry/TimeUtils.cs` is 56 lines of app helpers, not a scale/format-tagged type consumers can hold onto. |
| `astropy.coordinates` (frame-transform graph: ICRS, AltAz, Galactic, ...) | PARTIAL | `Astrometry/SOFA/Transform.cs` (1173 lines) does J2000<->JNow<->Apparent<->Topocentric with refraction (`Refco`), aberration (`Ab`), light deflection (`Ld`/`Ldsun`), geodetic conversion (`Gd2gc`) -- ERFA-grade math. It is one stateful `Transform` class (`SetJ2000`/`SetJNow`/`SetApparent`), not an extensible frame graph. `CoordinateUtils.cs` is ad hoc helpers (separation, pixel scale), not a coordinate type. |
| `astropy.wcs` (world coordinate systems, ~30 projections + distortion) | PARTIAL, one projection by design | `Astrometry/WCS.cs` supports TAN (gnomonic) + SIP forward-polynomial distortion (`SipPolynomial.cs`, fit via `PolynomialLeastSquares.cs`). Astropy wraps wcslib's full projection set (SIN, ARC, ZEA, AIT, ZPN, ...) plus SIP/TPV/DSS. TAN is the one amateur imaging actually uses -- see CLAUDE.md's plate-solving section, which never asks for another projection. **But see "Design notes" below for where TAN's limits become load-bearing** (wide-angle lens stitching / large mosaics), not just theoretical. |
| `reproject` (Astropy-affiliated, NOT core -- WCS-driven resampling/mosaicking: `reproject_interp`/`reproject_exact`) | NOT STARTED | No equivalent exists. [wcs-reprojection.md](wcs-reprojection.md) is the tracked plan to add exactly this: pull/inverse-warp resampling through `WCS.PixelToSky`/`SkyToPixel`, deliberately the opposite resampling direction from `DrizzleStrategy`/`DrizzleKernel` (push/forward-splat, correct for combining many incomplete dithered frames, wrong for resampling one complete source). Same niche as SWarp/Montage/AstroDrizzle in the wider ecosystem. |
| `astropy.constants` | PARTIAL | `Astrometry/Constants.cs` (63 lines) is a handful of app-specific constants, not a physical-constants table with values + uncertainties + units (G, c, M_sun, ...). |
| `astropy.units` (`Quantity`, unit-safe arithmetic) | NOT STARTED | Confirmed absent -- no `Quantity`/`Unit` type anywhere in the tree. Every value is a raw `double` carrying its unit by naming convention (`ra1Hours`, `pixelSizeUm`, `focalLengthMm` in `CoordinateUtils.cs`) or by XML-doc prose. **The single biggest structural gap versus Astropy's design** -- everything else here would get safer if it sat on top of this. |
| `astropy.table` (heterogeneous-column, unit-aware, serializable table) | NOT STARTED | No generic table type; the closest hit is `StarReferenceTable`, which is a domain-specific star-list POCO, not a reusable `Table`. |
| `astropy.modeling` (composable `Model` + `Fitter`) | NOT STARTED as a framework, PARTIAL as ad hoc fits | No shared model/fitter abstraction. Each site reimplements its own least-squares: `Astrometry/Focus/Hyperbola.cs` (V-curve autofocus), `PolynomialLeastSquares.cs` (SIP), `Astrometry/Comets/CometRateSolver.cs` (linear rate fit), plus the comet-integration background-plane fit (CLAUDE.md "Comet / moving-target integration"). |
| `astropy.stats` (sigma-clipping, biweight, bootstrap) | PARTIAL, different flavor | `Stat/` (`FFT.cs`, `DFT.cs`, `DSP.*.cs`, `PhaseCorrelation.cs`, `CatmullRomSpline.cs`, `StatisticsHelper.cs`) is signal-processing-shaped, closer to `scipy.signal` than to Astropy's robust-statistics toolkit. |
| `astropy.nddata` (uncertainty + mask + WCS carried on an array) | PARTIAL, domain-specific instead of generic | `Imaging/Channel.cs` / `Image.cs` carry a mask (`Image.Masks.cs`) and per-channel min/max, but there is no generic uncertainty array and no `CCDData`-equivalent type usable independent of the astro-imaging pipeline. |
| `astropy.io.fits` | N/A -- delegated | FITS I/O lives in the sibling `FITS.Lib` repo, not `TianWen.Lib`. The Lib-root `IO/` folder is one file, `AsciiRecordReader.cs` -- not a general I/O layer. |
| `astroquery` (Vizier/SIMBAD/JPL online catalog access) | N/A -- different philosophy, not a gap | `Astrometry/Catalogs/` (Tycho-2, NGC, SIMBAD merge) and `Astrometry/Comets/` (JPL SBDB/Horizons) are baked/cached offline artifacts, not live query wrappers. Deliberate: TianWen runs in the field with no internet, the opposite of Astropy's online-query-first model. |
| Solar-system ephemeris (Astropy typically defers to JPL kernels via `jplephem`) | DONE, self-contained | Full `Astrometry/VSOP87/` per-planet series + `MeeusMoon.cs` -- no external kernel dependency, which is a genuine advantage for the field-use case. |
| `astropy.cosmology` | N/A -- out of scope | No distance-ladder / cosmological use case for an imaging-automation tool. |
| `astropy.convolution` | N/A -- exists ad hoc instead | `Imaging/ATrousWaveletTransform.cs` is a real convolution implementation, just not exposed as a general kernel API. |
| `astropy.visualization` | N/A -- different layer | Rendering lives in the UI projects (`TianWen.UI.*`), not `TianWen.Lib`; see the stretch-pipeline architecture doc. |

## Deliberately different (not a gap)

- **Offline-baked catalogs and ephemeris caches instead of live queries.** Astropy's model assumes
  a network; TianWen's does not. This is the same category of decision as `Astrometry/Focus/` and
  `Astrometry/PlateSolve/` having no Astropy analogue at all -- they're domain-specific to imaging
  automation, not omissions.
- **A self-contained VSOP87 series instead of a JPL kernel dependency.** Astropy's usual path
  (`jplephem` + a downloaded kernel) is the *weaker* position for TianWen's use case, not the
  stronger one.

## Highest-leverage additions, ranked

1. **A `Quantity`/`Unit` type.** The most foundational gap -- WCS, coordinates and time would all
   become safer and more Astropy-like sitting on top of one, and every raw-`double`-plus-naming-
   convention call site is a latent unit-mismatch bug. Design sketch below.
2. **A frame-transform graph over the existing ERFA-grade math.** `Transform.cs`/`SofaFunctions.cs`
   already do the hard part; they are not exposed as composable frames the way `astropy.coordinates`
   is.
3. **Expand `WCS` beyond TAN** to the common projections (SIN, ARC, ZEA) -- moderate effort, given
   the SIP infrastructure already exists and only the projection math itself is missing. Design
   notes below on exactly when this stops being theoretical.
4. **A shared `Model`/`Fitter` abstraction** unifying `Hyperbola`, `PolynomialLeastSquares` and
   `CometRateSolver`'s independently-written least-squares fits.
5. **A generic, lightweight `Table` type** for star lists and catalog rows, replacing the
   domain-specific POCOs (`StarReferenceTable` and its siblings).

## Design notes: WCS geometry (gap 3), SIP order, and units in C# (gap 1)

### When TAN's limits become load-bearing: wide-angle lens stitching and large mosaics

`WCS.SkyToPixel` explicitly returns `null` when `cosC <= 0` ("behind the tangent plane") --
the textbook TAN/gnomonic failure mode. TAN is a rectilinear projection of the sky onto a flat
plane tangent at one point, so it cannot represent anything at or past 90 degrees from center, and
scale distortion grows nonlinearly well before that. The CD matrix itself is also only a
first-order linear approximation around `CRPix1`/`CRPix2`, valid for a few degrees at most,
independent of the deprojection nonlinearity.

This is fine, even ideal, for what it is built for: individual sub-frames (arcmin to a few
degrees) and the kind of modest multi-panel mosaic `FramingGrouper`'s tangent-plane fit targets (a
handful of overlapping narrow/wide-field panels spanning maybe up to ~10-20 degrees). **It is not
adequate as a single global projection for genuinely wide-angle work** -- an all-sky/fisheye rig,
or a 60-180 degree panorama. For that, two options, not mutually exclusive:

- A projection suited to large fields for the *output canvas*: ARC (zenithal equidistant) is the
  common choice for all-sky cameras since it preserves angular distance from center linearly; SIN
  (orthographic) is the natural fit for a true fisheye lens.
- The standard Montage-style approach: keep solving each tile in TAN (that part does not change)
  and reproject each onto a shared wide-area WCS for the final mosaic, rather than trying to fit
  one TAN over the whole thing.

If wide-angle lens stitching or large mosaics become an actual target (not just theoretical),
ranked gap 3 above moves from "nice to have" to load-bearing and should be pulled forward.

**Using `PixelToSky`/`SkyToPixel` generatively (to resample pixels, not just report a header) is
its own tracked plan: [wcs-reprojection.md](wcs-reprojection.md)** -- the direct analogue of
Astropy's affiliated `reproject` package (see the dedicated row above), covering single-frame SIP
undistort (P0) and multi-frame reprojection onto a shared WCS for mosaic stitching (P1), plus why
that is a deliberate pull/inverse-warp operation where drizzle is an equally deliberate
push/forward-splat one.

### Higher-order SIP: what it would take, and why it does not fix corner star shape

**SIP corrects centroid POSITION, never star SHAPE, and the two get conflated because both show
up "at the corners."** `WCS`'s SIP fields only ever appear inside `PixelToSky`/`SkyToPixel`,
warping the pixel<->sky *mapping*; nothing about a SIP polynomial touches pixel values or a star's
point-spread shape. "Stars get rounder toward the center, worse at the corners" (coma, astigmatism,
field curvature, sensor tilt) is a PSF-shape problem living one layer away, in the optics/imaging
pipeline, not in the WCS at all. TianWen already has the right tools queued for the actual problem,
not this one: `tianwen image correct-aberration` (coma/astigmatism/off-axis correction via an AI
model, [docs/todo/imaging.md](../todo/imaging.md) L361, not started) and per-chunk PSF
re-measurement for `INonStellarDeconvolver` to capture tilt/coma variation spatially
([docs/todo/imaging.md](../todo/imaging.md) L398, not started). On a rig with both real geometric
distortion (SIP's job) and real coma (the aberration tools' job), raising SIP order fixes only the
astrometry half; it will not visually round out a single star.

**What raising SIP order would actually take, today:**

- `SipPolynomial.MaxOrder` is a hard-coded `4` (`Astrometry/SipPolynomial.cs:30`); a fit above that
  throws. `CatalogPlateSolver.SipOrder` (`CatalogPlateSolver.cs:107`) defaults to `3` and is an
  `internal` fixed property today, not adaptive or configurable -- order 4 is already legal but
  unused.
- The free-parameter count per axis is `(order+1)(order+2)/2 - 1`: 9 at order 3, 14 at order 4.
  `MinMatchesForSipFit = 30` and the accept gate (`SkyToPixel`-residual RMS must beat the linear
  fit by at least 30%, sized to the `sqrt(K/N)` overfit-noise floor for `K` coefficients over `N`
  matches -- see the comment at `CatalogPlateSolver.cs:1140-1146`) were reasoned about at order 3's
  K. Raising the order without re-deriving both against the new K risks the opposite failure modes:
  either the fit is now almost always rejected (safe, useless), or the same absolute threshold
  quietly accepts an overfit polynomial that oscillates between the calibration stars (a Runge-type
  artifact) despite passing on RMS at the sample points.
- A raw pixel-offset polynomial fit conditions poorly past order ~4-5 without centering/scaling the
  input coordinates first; `PolynomialLeastSquares.cs` would need that if the ceiling moves.
- **A real silent-drop bug worth fixing regardless of whether the order is raised:** `WCS.
  ReadSipFromHeader` rejects any header whose `A_ORDER`/`B_ORDER`/etc. exceed `SipPolynomial.
  MaxOrder` by falling back to the linear WCS with **no log, no warning** (`WCS.cs`, the
  `maxOrder > SipPolynomial.MaxOrder` branch). A wide-field or fisheye rig commonly needs order 5+
  from ASTAP or astrometry.net to capture real pincushion/barrel distortion (see the wide-angle
  section above); ingesting such a solve today silently downgrades to linear-only with nothing to
  say so.
- The right long-term shape is probably: raise the ceiling deliberately (not just to "however high"
  -- pick it from what real wide-field solves need), make `SipOrder` scale with field size /
  measured residual pattern rather than a fixed constant regardless of frame, and turn the silent
  header-drop into at least a warning.

### A units type for the C# type system

Do not reach for a full SI dimensional-analysis system -- the actual vocabulary here is narrow
(angle, plus a handful of ratio types). The idiomatic C# 14 shape is a generic `Quantity<TUnit>`
keyed on a marker type with a static-abstract conversion factor: compile-time "can't add degrees
to arcseconds" safety, with no combinatorial explosion of wrapper structs.

```csharp
public interface IUnit<TSelf> where TSelf : IUnit<TSelf>
{
    static abstract double ToBaseFactor { get; }   // -> radians, for angle units
}

public readonly record struct Quantity<TUnit>(double Value) where TUnit : IUnit<TUnit>
{
    public double BaseValue => Value * TUnit.ToBaseFactor;
    public Quantity<TOther> To<TOther>() where TOther : IUnit<TOther>
        => new(BaseValue / TOther.ToBaseFactor);
}

public readonly struct Degrees : IUnit<Degrees> { public static double ToBaseFactor => Math.PI / 180.0; }
public readonly struct ArcSeconds : IUnit<ArcSeconds> { public static double ToBaseFactor => Math.PI / 648000.0; }

extension(double value)
{
    public Quantity<Degrees> Degrees() => new(value);
}
```

`readonly record struct` for zero allocation (this would sit in per-pixel/per-frame paths, same
reasoning as `Image.ResidentPlanes()`), operators defined only per-`TUnit` so mismatched units
simply do not compile, and a C# 14 extension block gives `42.0.Degrees()`-style construction.

Two scope calls, deliberate:

1. **Lives at the public API boundary** (WCS, `CoordinateUtils`, config), not inside the
   SOFA/`Transform` hot loops. Rewriting `SofaFunctions`'s ~1800 lines to use `Quantity`
   everywhere would be invasive and risks the same class of allocation/perf regression the
   residency work already got bitten by; keep the wrapper thin and unwrap to `double` before the
   numeric kernels.
2. **Do not generalize compound units** (arcsec/px, steps/°C) into a generic ratio type -- there
   are only two or three of those in the whole codebase. Matching the "three similar lines beats a
   premature abstraction" convention, hand-write `PixelScale`/`FocusTemperatureCoefficient` as
   their own small structs instead of a `Quantity<TNum, TDenom>` machinery.

## Maintenance rule

Update the STATUS cell here whenever one of these areas gets built out or a file moves. Do not let
this file say DONE while the underlying code still lacks the capability, or vice versa.
