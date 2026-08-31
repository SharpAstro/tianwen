using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// The wall-clock budget behind <c>docs/plans/plate-solver-performance.md</c>, on a real frame.
/// Every figure in that plan was hand-timed with a stopwatch and a fresh process -- good enough to
/// decide what to optimise, useless for noticing that an optimisation regressed. This is the
/// harness the plan asks for.
///
/// <para><b>The frame:</b> NGC 3576 (Statue of Liberty Nebula), SVBONY SV605CC / IMX533 at
/// 3008x3008, SH61 EDPH 270 mm f/4.5, 60 s, written by N.I.N.A. Already committed and already
/// linked into this project for <see cref="FindStarsBenchmarks"/>. It solves from nothing but its
/// own header -- 2.87"/px implied by <c>FOCALLEN</c> x pixel size over a 2.40 degree field, hint
/// from <c>OBJCTRA</c>/<c>OBJCTDEC</c> -- which is the premise the whole harness rests on, so it is
/// asserted in <see cref="GlobalSetup"/> rather than assumed. A frame that does not solve would
/// measure the failure path, at a completely different cost, and look like a win.</para>
///
/// <para><b>It is a Bayer mosaic and that is deliberate.</b> <c>SensorType.RGGB</c> means only "this
/// is a CFA mosaic"; the pattern itself rides in the Bayer offsets, which this file's <c>GRBG</c>
/// maps to (1, 0). <see cref="Image.FindStarsAsync"/> debayers to mono internally for such a frame
/// and corrects the half-pixel grid offset a 2x2 box debayer introduces, so solving the mosaic
/// directly is the real shipped path -- pre-debayering here would measure something the session
/// never does.</para>
///
/// <para><b>Read the MEDIAN, on a quiet box.</b> One invocation per iteration (forced by
/// <c>[IterationSetup]</c>) on work this long is inherently high-variance, and a machine doing
/// anything else widens it further -- measured medians repeated to within ~3% across runs while the
/// means moved 12%. That resolution is fine for what this harness is for: phases B and C target
/// 2-4x, not 10%. Do not read a 10% delta here as a regression.</para>
///
/// <para><b>What is NOT here, and why:</b> the ~590 ms catalog cold start, which is 51% of the
/// budget and all of phase B. It happens once per process and is cached thereafter, so it is
/// structurally not a BenchmarkDotNet job: BDN wants many iterations of a repeatable thing, and the
/// second iteration of a cold start is a warm start. Measure it the way the plan did -- whole-process
/// wall clock, fresh process per rep -- or with <c>RealFrameSolveProbe</c>, which reports it once.
/// Everything here runs with the catalog already warm, which is also what the second and subsequent
/// solves of a real session see.</para>
/// </summary>
[MemoryDiagnoser]
// RunStrategy.Monitoring, not the default throughput job. Two reasons, both forced by what is being
// measured: [IterationSetup] pins BDN to one invocation per iteration, and these operations run for
// tens to hundreds of milliseconds -- so the pilot/warmup machinery a throughput job runs buys
// nothing and ShortRunJob's three iterations cannot stabilise it (the detect benchmark reported an
// Error LARGER than its own mean). Monitoring plus more iterations gives a usable median.
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 12)]
public class PlateSolveBenchmarks
{
    private Image _frame = null!;
    private ImageDim _dim;
    private WCS _headerHint;
    private CelestialObjectDB _db = null!;
    private CatalogPlateSolver _solver = null!;
    private double _searchRadiusDeg;
    private double _dtJulianYears;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _frame = LoadRealFrame();

        _dim = _frame.GetImageDim()
            ?? throw new InvalidOperationException("the frame must imply a pixel scale (PIXSCALE, or pixel size x focal length)");

        var meta = _frame.ImageMeta;
        if (double.IsNaN(meta.TargetRA) || double.IsNaN(meta.TargetDec))
        {
            throw new InvalidOperationException("the frame must carry a pointing hint (OBJCTRA/OBJCTDEC)");
        }
        _headerHint = new WCS(meta.TargetRA, meta.TargetDec);

        // Mirrors SolveImageAsync: 0.75 of the longer field edge, and proper motion referred to the
        // exposure epoch (0 for a file with no plausible date, rather than a ~2000-year shift).
        _searchRadiusDeg = Math.Max(_dim.Width, _dim.Height) * _dim.PixelScale / 3600.0 * 0.75;
        _dtJulianYears = meta.ExposureStartTime.Year > 1900 ? meta.ExposureStartTime.JulianYearsSinceJ2000() : 0.0;

        _db = new CelestialObjectDB();
        await _db.InitDBAsync(waitForTycho2BulkLoad: true);
        _solver = new CatalogPlateSolver(_db, NullLogger<CatalogPlateSolver>.Instance);

        // Assert the premise ONCE, here, where it is free: every number below is meaningless if
        // this frame does not actually solve.
        var probe = await _solver.SolveImageAsync(_frame, _dim, searchOrigin: _headerHint);
        if (probe.Solution is null)
        {
            throw new InvalidOperationException(
                "the benchmark frame no longer solves from its header -- fix that before reading any timing below");
        }
    }

    /// <summary>
    /// Detection results are CACHED on the <see cref="Image"/> (a single slot keyed on the detection
    /// parameters), so without this every iteration after the first measures a cache-key comparison
    /// instead of the work. It is not a small distortion: the detect benchmark reported <b>50.87 ns</b>
    /// for finding 1,377 stars in a 9 MP frame, and the "full solve" quietly stopped including
    /// detection at all from its second iteration. A benchmark that measures nothing is worse than no
    /// benchmark, because someone will optimise against it.
    /// <para>Invalidating per iteration is also the honest model: a session solves a DIFFERENT frame
    /// every time, and so pays detection every time.</para>
    /// </summary>
    [IterationSetup]
    public void IterationSetup() => _frame.InvalidateStarListCache();

    /// <summary>
    /// The headline: a full hinted solve on a warm catalog. This is what phases A, C and D move.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Full hinted solve (warm catalog)")]
    public async Task<double> HintedSolve()
    {
        var result = await _solver.SolveImageAsync(_frame, _dim, searchOrigin: _headerHint);
        return result.Solution?.PixelScaleArcsec ?? double.NaN;
    }

    /// <summary>
    /// Star detection alone -- 7% of the plan's budget, and the input phase D would cap. Includes
    /// the internal mono debayer, because that is what detecting on a mosaic costs.
    /// </summary>
    [Benchmark(Description = "Detect stars (incl. mono debayer)")]
    public async Task<int> DetectStars()
    {
        var stars = await _frame.FindStarsAsync(channel: 0, snrMin: 10f);
        return stars.Count;
    }

    /// <summary>
    /// The catalog region query alone -- 5% of the budget, and the read phase B makes cheap. Warm,
    /// so this is the query, not the load.
    /// </summary>
    [Benchmark(Description = "Catalog region query (warm)")]
    public int CatalogQuery()
        // The solver's own query, with the radius and proper-motion epoch it derives internally, so
        // this measures the shipped call rather than a lookalike written beside it.
        => _solver.QueryCatalogStarsInRegion(_headerHint, _searchRadiusDeg, _dtJulianYears).Count;

    [GlobalCleanup]
    public void GlobalCleanup() => _frame?.Release();

    private static Image LoadRealFrame()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FITS", "imx533_sv605cc_60s_real.fits.gz");
        var tempPath = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks_platesolve_real.fits");
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            using var fileStream = File.OpenRead(path);
            using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
            using var outStream = File.Create(tempPath);
            gz.CopyTo(outStream);
        }

        // Deliberately NOT debayered: see the class remarks.
        return Image.TryReadFitsFile(tempPath, out var raw)
            ? raw
            : throw new InvalidDataException($"Failed to parse FITS at {tempPath}");
    }
}
