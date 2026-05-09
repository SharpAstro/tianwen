using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Loads filter transmission curves (<c>filter_curves.gs.gz</c>) and sensor QE
/// curves (<c>sensor_qe.gs.gz</c>) from embedded resources. Provides fuzzy
/// name-based lookup for both. Use <see cref="LoadAsync"/> once at startup.
/// </summary>
public static class FilterCurveDatabase
{
    private static readonly ConcurrentDictionary<string, FilterCurve> _filtersByNormalizedName = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, FilterCurve> _sensorsByNormalizedName = new(StringComparer.Ordinal);
    private static ImmutableArray<FilterCurve> _allFilters = ImmutableArray<FilterCurve>.Empty;
    private static ImmutableArray<FilterCurve> _allSensors = ImmutableArray<FilterCurve>.Empty;
    private static ImmutableArray<FilterCurve> _allSeds = ImmutableArray<FilterCurve>.Empty;
    // Sorted by synthetic B-V for binary-search lookup via TryGetSedByBv
    private static ImmutableArray<(double Bv, FilterCurve Sed)> _sedsByBv = ImmutableArray<(double, FilterCurve)>.Empty;
    private static Task? _loadTask;
    private static int _loaded;

    public static bool IsLoaded => _loaded != 0;

    /// <summary>All loaded filter curves in insertion order.</summary>
    public static ImmutableArray<FilterCurve> AllFilters => _allFilters;

    /// <summary>All loaded sensor QE curves in insertion order.</summary>
    public static ImmutableArray<FilterCurve> AllSensors => _allSensors;

    /// <summary>All loaded Pickles stellar SEDs.</summary>
    public static ImmutableArray<FilterCurve> AllSeds => _allSeds;

    /// <summary>Backward-compat alias for <see cref="AllFilters"/>.</summary>
    public static ImmutableArray<FilterCurve> AllCurves => _allFilters;

    /// <summary>
    /// Loads <c>filter_curves.gs.gz</c>, <c>sensor_qe.gs.gz</c>, and
    /// <c>pickles_sed.gs.gz</c> from embedded resources.
    /// Idempotent — subsequent calls are no-ops.
    /// </summary>
    public static ValueTask LoadAsync(CancellationToken ct = default)
    {
        // Fast path: already loaded (check before taking the lock)
        if (_loadTask is { IsCompletedSuccessfully: true })
            return ValueTask.CompletedTask;

        // Serialise initialisation through a single Task — subsequent callers
        // await the same Task instead of racing on the partially-populated state.
        var existing = Interlocked.CompareExchange(ref _loadTask,
            Task.Run(() => DoLoad(ct)), null);
        if (existing is not null)
            return new ValueTask(existing);

        Interlocked.Exchange(ref _loaded, 1);
        return new ValueTask(_loadTask);
    }

    private static void DoLoad(CancellationToken ct)
    {
        var assembly = typeof(FilterCurveDatabase).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();

        LoadResource(assembly, manifestNames, "filter_curves.gs.gz", _filtersByNormalizedName,
            indexOriginStems: true, out var filters);
        ImmutableInterlocked.Update(ref _allFilters, (current, incoming) =>
            current.AddRange(incoming), filters);

        LoadResource(assembly, manifestNames, "sensor_qe.gs.gz", _sensorsByNormalizedName,
            indexOriginStems: false, out var sensors);
        ImmutableInterlocked.Update(ref _allSensors, (current, incoming) =>
            current.AddRange(incoming), sensors);

        // Sensor model names are short and unambiguous (e.g. "IMX533") —
        // index by un-normalised upper-case EXTNAME for exact case-insensitive lookup.
        foreach (var s in _allSensors)
            _sensorsByNormalizedName.TryAdd(s.Name.ToUpperInvariant(), s);

        // Load Pickles stellar SEDs and precompute canonical B-V index
        LoadResource(assembly, manifestNames, "pickles_sed.gs.gz",
            new ConcurrentDictionary<string, FilterCurve>(StringComparer.Ordinal),
            indexOriginStems: false, out var seds);
        ImmutableInterlocked.Update(ref _allSeds, (_, incoming) => incoming, seds);
        PrecomputeSedBvIndex();
    }

    /// <summary>
    /// Builds a sorted index of Pickles SEDs by canonical Johnson B-V colour
    /// index via a standard spectral-type calibration. Pickles SEDs are relative
    /// (not absolute-flux-calibrated), so synthetic photometry gives the correct
    /// ordering but not the absolute B-V values. Canonical values come from
    /// Schmidt-Kaler / Allen's Astrophysical Quantities.
    /// </summary>
    private static void PrecomputeSedBvIndex()
    {
        var builder = ImmutableArray.CreateBuilder<(double Bv, FilterCurve Sed)>(_allSeds.Length);
        foreach (var sed in _allSeds)
        {
            if (TryGetCanonicalBv(sed.Name, out var bv))
                builder.Add((bv, sed));
        }
        builder.Sort((a, b) => a.Bv.CompareTo(b.Bv));
        _sedsByBv = builder.ToImmutable();
    }

    /// <summary>
    /// Returns the canonical Johnson B-V for a Pickles spectral type string
    /// (e.g. "G2V" → 0.65). Based on Schmidt-Kaler (1982) anchor points with
    /// linear interpolation between adjacent known values.
    /// Non-stellar SEDs (GALAXY_*) are skipped.
    /// </summary>
    private static bool TryGetCanonicalBv(string spectralType, out double bv)
    {
        bv = 0;
        if (string.IsNullOrEmpty(spectralType) || spectralType.StartsWith("GALAXY_", StringComparison.Ordinal))
            return false;

        // Parse: spectral class letter + subclass number + luminosity suffix
        var span = spectralType.AsSpan().Trim();
        if (span.Length < 1) return false;

        var spClass = span[0];
        var rest = span[1..];
        var nDigits = 0;
        while (nDigits < rest.Length && (char.IsDigit(rest[nDigits]) || rest[nDigits] == '.'))
            nDigits++;
        var subclass = nDigits > 0 && double.TryParse(rest[..nDigits],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var sc) ? sc : 5.0;
        var lc = nDigits < rest.Length ? rest[nDigits..].ToString() : "V";

        // Schmidt-Kaler (1982) anchor points: (subclass, B-V) pairs.
        // Interpolate subclass linearly between anchors.
        if (!BvAnchors.TryGetValue(spClass, out var lcTables)) return false;
        if (!lcTables.TryGetValue(lc, out var anchors))
        {
            // Fall back to V if this luminosity class isn't tabulated
            if (!lcTables.TryGetValue("V", out anchors)) return false;
        }

        bv = InterpolateAnchors(anchors, subclass);
        return true;
    }

    /// <summary>Piecewise-linear interpolation between (subclass, B-V) anchor points.</summary>
    private static double InterpolateAnchors((double Subclass, double Bv)[] anchors, double subclass)
    {
        if (subclass <= anchors[0].Subclass) return anchors[0].Bv;
        if (subclass >= anchors[^1].Subclass) return anchors[^1].Bv;

        for (var i = 0; i < anchors.Length - 1; i++)
        {
            if (subclass <= anchors[i + 1].Subclass)
            {
                var t = (subclass - anchors[i].Subclass) /
                        (anchors[i + 1].Subclass - anchors[i].Subclass);
                return anchors[i].Bv + t * (anchors[i + 1].Bv - anchors[i].Bv);
            }
        }
        return anchors[^1].Bv;
    }

    /// <summary>
    /// Canonical B-V anchor points from Schmidt-Kaler (1982) + Cox (2000).
    /// Keyed by spectral class letter, then by luminosity class suffix.
    /// Each anchor is (subclass, B-V). Values between anchors are interpolated.
    /// </summary>
    private static readonly Dictionary<char, Dictionary<string, (double Subclass, double Bv)[]>> BvAnchors = new()
    {
        ['O'] = new() { ["V"] = [(5, -0.33), (9, -0.31)] },
        ['B'] = new()
        {
            ["V"]   = [(0, -0.30), (5, -0.16), (9, -0.06)],
            ["III"] = [(0, -0.25), (5, -0.16), (9, -0.05)],
            ["I"]   = [(0, -0.20), (5, -0.08), (9, +0.03)],
            ["II"]  = [(0, -0.23), (5, -0.12), (9, -0.02)],
            ["IV"]  = [(0, -0.28), (5, -0.17), (9, -0.07)],
        },
        ['A'] = new()
        {
            ["V"]   = [(0, 0.00), (5, 0.15), (9, 0.30)],
            ["III"] = [(0, 0.05), (5, 0.24), (9, 0.36)],
            ["I"]   = [(0, 0.10), (5, 0.30), (9, 0.43)],
            ["II"]  = [(0, 0.08), (5, 0.27), (9, 0.39)],
            ["IV"]  = [(0, 0.02), (5, 0.20), (9, 0.33)],
        },
        ['F'] = new()
        {
            ["V"]   = [(0, 0.30), (5, 0.44), (9, 0.56)],
            ["III"] = [(0, 0.35), (5, 0.46), (9, 0.55)],
            ["I"]   = [(0, 0.45), (5, 0.53), (9, 0.65)],
            ["II"]  = [(0, 0.40), (5, 0.50), (9, 0.60)],
            ["IV"]  = [(0, 0.33), (5, 0.45), (9, 0.56)],
        },
        ['G'] = new()
        {
            ["V"]   = [(0, 0.58), (2, 0.65), (5, 0.68), (8, 0.74)],
            ["III"] = [(0, 0.65), (5, 0.85), (8, 0.95)],
            ["I"]   = [(0, 0.80), (5, 1.05), (8, 1.18)],
            ["II"]  = [(0, 0.72), (5, 0.95), (8, 1.06)],
            ["IV"]  = [(0, 0.62), (5, 0.77), (8, 0.83)],
        },
        ['K'] = new()
        {
            ["V"]   = [(0, 0.81), (2, 0.91), (5, 1.15), (7, 1.33)],
            ["III"] = [(0, 1.00), (2, 1.16), (5, 1.45), (7, 1.53)],
            ["I"]   = [(0, 1.20), (2, 1.35), (5, 1.58), (7, 1.65)],
            ["II"]  = [(0, 1.10), (2, 1.26), (5, 1.51), (7, 1.59)],
            ["IV"]  = [(0, 0.90), (5, 1.25)],
        },
        ['M'] = new()
        {
            ["V"]   = [(0, 1.40), (2, 1.49), (5, 1.64), (7, 1.79)],
            ["III"] = [(0, 1.22), (2, 1.32), (5, 1.55), (7, 1.65)],
            ["I"]   = [(0, 1.40), (2, 1.48), (5, 1.65), (7, 1.75)],
            ["II"]  = [(0, 1.28), (2, 1.38), (5, 1.60), (7, 1.70)],
        },
    };

    private static void LoadResource(
        Assembly assembly, string[] manifestNames, string resourceName,
        ConcurrentDictionary<string, FilterCurve> dict, bool indexOriginStems,
        out ImmutableArray<FilterCurve> curves)
    {
        curves = ImmutableArray<FilterCurve>.Empty;

        var manifest = manifestNames.FirstOrDefault(p => p.EndsWith("." + resourceName, StringComparison.Ordinal));
        if (manifest is null) return;

        using var stream = assembly.GetManifestResourceStream(manifest);
        if (stream is null) return;

        byte[] decompressed;
        using (var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            decompressed = ms.ToArray();
        }

        var builder = ImmutableArray.CreateBuilder<FilterCurve>();
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(decompressed))
        {
            var rec = recMem.Span;
            if (rec.IsEmpty) continue;

            var name     = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            var origin   = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            var nPoints  = (int)AsciiRecordReader.ReadDouble(AsciiRecordReader.TakeField(ref rec));
            var wlBytes  = AsciiRecordReader.TakeField(ref rec);
            var tpBytes  = AsciiRecordReader.TakeField(ref rec); // last field

            var wavelengths = AsciiRecordReader.ReadDoubleArray(wlBytes);
            var throughputs = AsciiRecordReader.ReadDoubleArray(tpBytes);

            var curve = new FilterCurve(
                name,
                origin,
                ImmutableArray.Create(wavelengths),
                ImmutableArray.Create(throughputs));

            builder.Add(curve);
            dict[NormalizeName(name)] = curve;

            // For filters: also index by origin filename stem (without extension)
            // to help match user-provided names. Sensors don't need this.
            if (indexOriginStems)
            {
                var originStem = Path.GetFileNameWithoutExtension(origin);
                if (originStem.Length > 0)
                    dict.TryAdd(NormalizeName(originStem), curve);
            }
        }

        curves = builder.ToImmutable();
    }

    // ------------------------------------------------------------------ Filters

    /// <summary>
    /// Looks up the exact filter curve by SASP internal name (EXTNAME).
    /// </summary>
    public static bool TryGetFilter(string filterName, [NotNullWhen(true)] out FilterCurve curve)
    {
        curve = default;
        if (!IsLoaded) return false;
        return _filtersByNormalizedName.TryGetValue(NormalizeName(filterName), out curve);
    }

    /// <summary>
    /// Finds the best-matching filter curve for a user-provided filter name via
    /// token overlap. See <see cref="Tokenize"/> and <see cref="TokenizeFromUnderscores"/>.
    /// </summary>
    public static bool TryMatchFilter(string userFilterName, [NotNullWhen(true)] out FilterCurve curve)
    {
        curve = default;
        if (!IsLoaded || string.IsNullOrWhiteSpace(userFilterName)) return false;

        var needle = NormalizeName(userFilterName);
        if (needle.Length == 0) return false;

        if (_filtersByNormalizedName.TryGetValue(needle, out curve))
            return true;

        var needleTokens = Tokenize(userFilterName);
        if (needleTokens.Count == 0) return false;

        FilterCurve? best = null;
        var bestScore = int.MinValue;

        foreach (var entry in _allFilters)
        {
            var keyTokens = TokenizeFromUnderscores(entry.Name);
            var shared = 0;
            foreach (var nt in needleTokens)
                foreach (var kt in keyTokens)
                    if (string.Equals(nt, kt, StringComparison.Ordinal))
                        shared++;

            if (shared == 0)
            {
                if (needleTokens.Count == 1 && needleTokens[0].Length <= 2 &&
                    NormalizeName(entry.Name).EndsWith(needleTokens[0], StringComparison.Ordinal))
                    shared = 1;
                else continue;
            }

            // Gate: at least half the key's tokens must be covered
            if (shared * 2 < keyTokens.Count) continue;

            var extraTokens = keyTokens.Count - shared;
            var score = shared * 10 - extraTokens;
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        if (bestScore > 0)
        {
            curve = best!.Value;
            return true;
        }
        return false;
    }

    // Keep backward compat aliases
    public static bool TryGetCurve(string filterName, [NotNullWhen(true)] out FilterCurve curve)
        => TryGetFilter(filterName, out curve);
    public static bool TryMatchCurve(string userFilterName, [NotNullWhen(true)] out FilterCurve curve)
        => TryMatchFilter(userFilterName, out curve);

    // ------------------------------------------------------------------ Sensors

    /// <summary>
    /// Looks up a sensor QE curve by sensor die model, e.g. "IMX533" or "imx533".
    /// Matching is case-insensitive; returns false if not loaded or unknown.
    /// </summary>
    public static bool TryGetSensor(string sensorModel, [NotNullWhen(true)] out FilterCurve curve)
    {
        curve = default;
        if (!IsLoaded || string.IsNullOrWhiteSpace(sensorModel)) return false;
        return _sensorsByNormalizedName.TryGetValue(sensorModel.ToUpperInvariant(), out curve);
    }

    /// <summary>
    /// Tries to find a sensor QE curve from a camera product name string (e.g.
    /// "ZWO ASI533MC Pro") by extracting numeric model identifiers and matching
    /// against known sensor names.
    /// </summary>
    public static bool TryMatchSensor(string productName, [NotNullWhen(true)] out FilterCurve curve)
    {
        curve = default;
        if (!IsLoaded || string.IsNullOrWhiteSpace(productName)) return false;

        // Try exact match first (case-insensitive)
        if (_sensorsByNormalizedName.TryGetValue(productName.ToUpperInvariant(), out curve))
            return true;

        var needle = NormalizeName(productName);

        // Try normalized substring match (handles "IMX533" in "ZWO ASI533MC Pro"
        // only if the product name happens to contain the sensor prefix)
        foreach (var (key, c) in _sensorsByNormalizedName)
        {
            if (key.Length >= 4 && needle.Contains(key, StringComparison.Ordinal))
            {
                curve = c;
                return true;
            }
        }

        // Extract numeric model from product name (e.g. "533" from "ASI533MC")
        // and match against sensor names — prefer shorter matching keys
        var numbers = ExtractNumbers(needle);
        FilterCurve? bestByNumber = null;
        var bestKeyLength = int.MaxValue;
        foreach (var num in numbers)
        {
            if (num.Length < 3) continue;
            foreach (var (key, c) in _sensorsByNormalizedName)
            {
                if (key.Contains(num, StringComparison.Ordinal) && key.Length < bestKeyLength)
                {
                    bestKeyLength = key.Length;
                    bestByNumber = c;
                }
            }
        }
        if (bestByNumber is { } bbn)
        {
            curve = bbn;
            return true;
        }

        return false;
    }

    /// <summary>Extracts contiguous digit sequences from a normalised string.</summary>
    private static List<string> ExtractNumbers(string normalised)
    {
        var results = new List<string>(2);
        var start = -1;
        for (var i = 0; i <= normalised.Length; i++)
        {
            if (i < normalised.Length && char.IsDigit(normalised[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                results.Add(normalised[start..i]);
                start = -1;
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ System throughput

    /// <summary>
    /// Computes the combined system throughput for a given sensor and set of
    /// optical filters: T_sys(λ) = QE(λ) × filter₁(λ) × filter₂(λ) × …
    /// Returns null if the sensor model or any filter name cannot be resolved.
    /// </summary>
    public static FilterCurve? ComputeSystemThroughput(string sensorModel, params string[] filterNames)
    {
        if (!TryGetSensor(sensorModel, out var qe))
            return null;

        var curves = new List<FilterCurve>(filterNames.Length + 1) { qe };
        foreach (var fn in filterNames)
        {
            if (TryMatchFilter(fn, out var fc))
                curves.Add(fc);
            else
                return null;
        }

        var sensorName = sensorModel.Length > 0 ? sensorModel : "UnknownSensor";
        return FilterCurve.Combine($"{sensorName}+{string.Join("+", filterNames)}",
            curves.ToArray().AsSpan());
    }

    /// <summary>
    /// Builds per-channel system throughput curves from image metadata.
    /// For OSC cameras (SensorType.RGGB), includes Sony colour sensor CFA curves.
    /// For mono cameras, <paramref name="redFilter"/>/<paramref name="greenFilter"/>/
    /// <paramref name="blueFilter"/> must be provided per channel.
    /// The additional optical filter from the image (e.g. LP, UV/IR cut) is included
    /// in all channels when its name can be resolved.
    /// </summary>
    public static (FilterCurve R, FilterCurve G, FilterCurve B)? BuildChannelThroughputs(
        ImageMeta meta,
        string? redFilter = null,
        string? greenFilter = null,
        string? blueFilter = null)
    {
        if (!IsLoaded) return null;

        var sensorModel = meta.SensorModel is { Length: > 0 } sm ? sm
            : meta.Instrument; // fall back to instrument name

        // Try to find a sensor QE curve; non-Sony/IMX sensors (Canon, etc.) won't match.
        TryGetSensor(sensorModel, out var qe);
        if (qe.Name is null) TryMatchSensor(sensorModel, out qe);
        var hasQe = qe.Name is not null;

        // Additional optical filter (LP, UV/IR cut, etc.)
        var opticalFilterName = meta.Filter.FilterNameForFits;
        var hasOpticalFilter = opticalFilterName is { Length: > 0 }
            && opticalFilterName != meta.Filter.DisplayName; // skip bare coarse name

        // For OSC, look up the per-channel CFA curves.
        // Canon/Nikon/Sony CFA curves are in the database keyed by brand.
        FilterCurve? cfaR = null, cfaG = null, cfaB = null;
        if (meta.SensorType == SensorType.RGGB)
        {
            TryMatchFilter("SONY_COLOR_SENSOR_R", out var cr);
            TryMatchFilter("SONY_COLOR_SENSOR_G", out var cg);
            TryMatchFilter("SONY_COLOR_SENSOR_B", out var cb);
            cfaR = cr; cfaG = cg; cfaB = cb;
        }

        // Build per-channel curves: QE × CFA × filter × optical
        var curvesR = new List<FilterCurve>(4);
        var curvesG = new List<FilterCurve>(4);
        var curvesB = new List<FilterCurve>(4);

        if (hasQe)
        {
            curvesR.Add(qe); curvesG.Add(qe); curvesB.Add(qe);
        }
        if (cfaR is { } cr2) curvesR.Add(cr2);
        if (cfaG is { } cg2) curvesG.Add(cg2);
        if (cfaB is { } cb2) curvesB.Add(cb2);

        var rName = redFilter ?? (meta.SensorType == SensorType.RGGB ? "CFA_R" : null);
        var gName = greenFilter ?? (meta.SensorType == SensorType.RGGB ? "CFA_G" : null);
        var bName = blueFilter ?? (meta.SensorType == SensorType.RGGB ? "CFA_B" : null);

        if (rName is { Length: > 0 } && TryMatchFilter(rName, out var rf)) curvesR.Add(rf);
        if (gName is { Length: > 0 } && TryMatchFilter(gName, out var gf)) curvesG.Add(gf);
        if (bName is { Length: > 0 } && TryMatchFilter(bName, out var bf)) curvesB.Add(bf);

        if (hasOpticalFilter && TryMatchFilter(opticalFilterName, out var ofc2))
        {
            curvesR.Add(ofc2);
            curvesG.Add(ofc2);
            curvesB.Add(ofc2);
        }

        if (curvesR.Count == 0 || curvesG.Count == 0 || curvesB.Count == 0)
            return null;

        var tsysR = curvesR.Count == 1 ? curvesR[0] : FilterCurve.Combine("tsysR", curvesR.ToArray().AsSpan());
        var tsysG = curvesG.Count == 1 ? curvesG[0] : FilterCurve.Combine("tsysG", curvesG.ToArray().AsSpan());
        var tsysB = curvesB.Count == 1 ? curvesB[0] : FilterCurve.Combine("tsysB", curvesB.ToArray().AsSpan());

        return (tsysR, tsysG, tsysB);
    }

    // ------------------------------------------------------------------ SEDs (Pickles)

    /// <summary>
    /// Returns the Pickles SED whose synthetic Johnson B-V is closest to
    /// <paramref name="bv"/>. Returns false if SEDs are not loaded or the
    /// B-V value is outside the Pickles library range.
    /// </summary>
    public static bool TryGetSedByBv(double bv, [NotNullWhen(true)] out FilterCurve sed)
    {
        sed = default;
        if (_sedsByBv.Length == 0 || double.IsNaN(bv)) return false;

        // Binary search for closest B-V
        var seds = _sedsByBv;
        var lo = 0;
        var hi = seds.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (seds[mid].Bv < bv) lo = mid + 1;
            else hi = mid;
        }

        // Check neighbours for closest match
        var bestIdx = lo;
        var bestDist = Math.Abs(seds[lo].Bv - bv);
        if (lo > 0 && Math.Abs(seds[lo - 1].Bv - bv) < bestDist)
        {
            bestIdx = lo - 1;
            bestDist = Math.Abs(seds[lo - 1].Bv - bv);
        }
        if (lo < seds.Length - 1 && Math.Abs(seds[lo + 1].Bv - bv) < bestDist)
            bestIdx = lo + 1;

        sed = seds[bestIdx].Sed;
        return true;
    }

    /// <summary>
    /// Looks up a Pickles SED by exact spectral type name (EXTNAME), e.g. "G2V" or "M5III".
    /// Case-insensitive.
    /// </summary>
    public static bool TryGetSedByName(string spectralType, [NotNullWhen(true)] out FilterCurve sed)
    {
        sed = default;
        if (!IsLoaded || string.IsNullOrWhiteSpace(spectralType)) return false;
        var needle = spectralType.ToUpperInvariant();
        foreach (var s in _allSeds)
        {
            if (string.Equals(s.Name, needle, StringComparison.OrdinalIgnoreCase))
            {
                sed = s;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes expected per-channel flux ratios (R/G, B/G) for a given stellar
    /// SED viewed through per-channel system throughput curves.
    /// The G channel is the reference (ratio = 1.0).
    /// </summary>
    public static (double RG, double BG)? ComputeExpectedRatios(
        FilterCurve sed, FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB)
    {
        var fluxR = FilterCurve.IntegrateSedThroughput(sed, tsysR);
        var fluxG = FilterCurve.IntegrateSedThroughput(sed, tsysG);
        var fluxB = FilterCurve.IntegrateSedThroughput(sed, tsysB);
        if (fluxG <= 0) return null;
        return (fluxR / fluxG, fluxB / fluxG);
    }

    // ------------------------------------------------------------------ Token helpers

    internal static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        Span<char> buffer = stackalloc char[name.Length];
        var w = 0;
        foreach (var c in name)
            if (char.IsAsciiLetterOrDigit(c))
                buffer[w++] = char.ToLowerInvariant(c);
        return new string(buffer[..w]);
    }

    private static List<string> Tokenize(string name)
    {
        var tokens = new List<string>(6);
        var span = name.AsSpan();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && char.IsAsciiLetterOrDigit(span[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                var token = span[start..i].ToString();
                tokens.Add(token.ToLowerInvariant());
                start = -1;
            }
        }
        return tokens;
    }

    private static List<string> TokenizeFromUnderscores(string name)
    {
        var parts = name.Split(['_', '/'], StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>(parts.Length * 2);
        foreach (var part in parts)
        {
            var subTokens = Tokenize(part);
            tokens.AddRange(subTokens);
        }
        return tokens;
    }
}
