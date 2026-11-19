using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>One star of an <see cref="AstapStarDatabase"/>.</summary>
/// <param name="RA">Right ascension in hours, at the database's <see cref="AstapStarDatabase.Epoch"/>.</param>
/// <param name="Dec">Declination in degrees.</param>
/// <param name="Magnitude">The database's magnitude (<see cref="AstapStarDatabase.MagnitudeBand"/>), to a tenth.</param>
public readonly record struct AstapStar(double RA, double Dec, float Magnitude);

/// <summary>The stars of a cone of sky, and how deep the database reached there.</summary>
/// <param name="Stars">The stars inside the cone no fainter than the magnitude asked for, brightest first.</param>
/// <param name="StarsInCone">Every star the database holds inside the cone, whatever its magnitude.</param>
/// <param name="AreaSquareDegrees">The cone's area.</param>
/// <param name="FaintestMagnitude">The faintest magnitude the database holds inside the cone; NaN when it holds no star there.</param>
/// <param name="DensityLimited">True when the cone holds at least nine tenths of the database's density cap, so the database
/// was cut brightest-first there and stars fainter than about <paramref name="FaintestMagnitude"/> are missing: the sign that a
/// deeper source is needed for this field.</param>
public sealed record AstapStarField(ImmutableArray<AstapStar> Stars, int StarsInCone, double AreaSquareDegrees, float FaintestMagnitude, bool DensityLimited);

/// <summary>
/// A star database as the ASTAP plate solver installs it (Han Kleijn's HNSKY format: <c>d50_0101.1476</c> and its siblings),
/// read in place, so a machine that has ASTAP needs no download to know the Gaia stars of a field (#1685, the owner's
/// ladder of sources: the installed one first).
/// </summary>
/// <remarks>
/// <para><b>Written from the format's description and checked against the sky, not taken from ASTAP's source</b>, which is
/// GPL. A file is a 110-byte text header (its last byte the record size) and then 5-byte records. A record whose first three
/// bytes are FF FF FF is a section header: its fourth byte is the declination's high byte plus 128 and its fifth the
/// magnitude times ten plus 16, and every record after it inherits both. Any other record is a star: three bytes of right
/// ascension (24 h over 2^24 - 1, low byte first) and the declination's two low bytes (90 degrees over 2^23 - 1). The worked
/// example of the description is Sirius: C3 06 48 and D7 39, with E8 (-24) as the declination's high byte, give 6.75247662 h
/// and -16.7161401 degrees.</para>
/// <para><b>Measured 2026-11-19 on the installed d50</b> against Gaia DR3 from VizieR over 29.5 square degrees of Carina:
/// positions agree to a median 0.19 arcsec, the magnitude is Gaia's BP to within 0.04, and 98.7 to 99.8 % of Gaia's stars
/// from BP 8 to 14 are there. The database is cut brightest-first at its density cap (5,194 stars a square degree found
/// against the header's 5,000), which in that field is BP 15: 90 % of the stars at 14 to 15, 21 % at 15 to 16, none
/// beyond 17. <see cref="AstapStarField.DensityLimited"/> says when a field ran into that cut.</para>
/// <para>The sky is split into rings of declination, each cut into cells, one file a cell (<c>name_RRNN.areas</c>). Which
/// ring covers which declinations, and which cell which right ascensions, is read off the files themselves the first time a
/// ring is needed and kept, so nothing here assumes a geometry: a database of another density or another split reads the
/// same way. Only 5-byte records are read; the 6-byte kind that adds a colour was not at hand to check and is refused.</para>
/// <para>Positions are at the database's <see cref="Epoch"/> (2025 for the databases of 2025), not J2000.</para>
/// </remarks>
public sealed partial class AstapStarDatabase
{
    /// <summary>The text header's length; its last byte is the record size.</summary>
    public const int HeaderLength = 110;

    /// <summary>The environment variable that names a folder to look in before the installed ones.</summary>
    public const string FolderVariable = "TIANWEN_ASTAP_FOLDER";

    private const double RaHoursPerUnit = 24.0 / ((1 << 24) - 1);
    private const double DecDegreesPerUnit = 90.0 / ((1 << 23) - 1);
    private const double RingPaddingDegrees = 0.05;
    private static readonly ImmutableArray<string> WindowsProgramFolders = ["ProgramW6432", "ProgramFiles"];

    private readonly ImmutableArray<(int Ring, ImmutableArray<string> Files)> _rings;
    private readonly ConcurrentDictionary<int, Task<ImmutableArray<Cell>>> _cells = new();
    private Task<ImmutableArray<RingSpan>>? _ringSpans;

    private AstapStarDatabase(string folder, string name, int areas, string description, ImmutableArray<(int Ring, ImmutableArray<string> Files)> rings)
    {
        Folder = folder;
        Name = name;
        Areas = areas;
        Description = description;
        _rings = rings;
        MaxStarsPerSquareDegree = DensityPattern().Match(description) is { Success: true } d ? int.Parse(d.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : null;
        Epoch = EpochPattern().Match(description) is { Success: true } e ? double.Parse(e.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : null;
        MagnitudeBand = BandPattern().Match(description) is { Success: true } b ? b.Groups[1].Value : null;
    }

    /// <summary>The folder the files are in.</summary>
    public string Folder { get; }

    /// <summary>The database's name, the three characters its files begin with (<c>d50</c>, <c>g05</c>).</summary>
    public string Name { get; }

    /// <summary>How many cells the sky is split into, the files' extension (1476, 290).</summary>
    public int Areas { get; }

    /// <summary>The text its files carry: what the stars are, the density cap, the epoch, the magnitude's band.</summary>
    public string Description { get; }

    /// <summary>The density the database was cut at, stars a square degree, where its header says so.</summary>
    public int? MaxStarsPerSquareDegree { get; }

    /// <summary>The epoch of the positions, where the header says so.</summary>
    public double? Epoch { get; }

    /// <summary>The magnitude's band (<c>BP</c>), where the header says so.</summary>
    public string? MagnitudeBand { get; }

    /// <summary>How many files (cells) the folder holds for this database.</summary>
    public int FileCount => _rings.Sum(static r => r.Files.Length);

    [GeneratedRegex(@"density<=\s*(\d+)")]
    private static partial Regex DensityPattern();

    [GeneratedRegex(@"Epoch=\s*(\d+(?:\.\d+)?)")]
    private static partial Regex EpochPattern();

    [GeneratedRegex(@"Magnitude is\s+(\w+)")]
    private static partial Regex BandPattern();

    /// <summary>
    /// Where an installed ASTAP keeps its databases, most specific first: the folder <see cref="FolderVariable"/> names, then
    /// the installer's folder on Windows (beside <c>astap_cli.exe</c>) and <c>/opt/astap</c> on Linux. A folder that does not
    /// exist is still listed; <see cref="Discover"/> answers with nothing for it.
    /// </summary>
    public static IEnumerable<string> InstallFolders()
    {
        if (Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } named)
        {
            yield return named;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var variable in WindowsProgramFolders)
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } programs)
                {
                    yield return Path.Combine(programs, "astap");
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/opt/astap";
        }
    }

    /// <summary>The deepest database of the first install folder that holds one; null where ASTAP has none on this machine.</summary>
    public static AstapStarDatabase? FindInstalled()
    {
        foreach (var folder in InstallFolders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Discover(folder) is [var deepest, ..])
            {
                return deepest;
            }
        }
        return null;
    }

    /// <summary>
    /// The databases a folder holds, the densest first (by the header's density cap, then by the number of cells). A file
    /// belongs to a database by its name alone: three characters, an underscore, two digits of ring and two of cell, and the
    /// number of cells as its extension.
    /// </summary>
    public static ImmutableArray<AstapStarDatabase> Discover(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var families = new Dictionary<(string Name, int Areas), SortedDictionary<int, List<string>>>();
        foreach (var path in FileEnumeration.Enumerate(folder, recursive: false, static (ref FileSystemEntry entry) => !entry.IsDirectory && TryParseName(entry.FileName, out _, out _, out _)))
        {
            if (TryParseName(Path.GetFileName(path.AsSpan()), out var name, out var areas, out var ring))
            {
                var key = (name.ToString().ToLowerInvariant(), areas);
                if (!families.TryGetValue(key, out var rings))
                {
                    families[key] = rings = [];
                }
                if (!rings.TryGetValue(ring, out var files))
                {
                    rings[ring] = files = [];
                }
                files.Add(path);
            }
        }
        var found = new List<AstapStarDatabase>(families.Count);
        foreach (var ((name, areas), rings) in families)
        {
            var ordered = rings.Select(static r => (r.Key, r.Value.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray())).ToImmutableArray();
            if (TryReadDescription(ordered[0].Item2[0]) is { } description)
            {
                found.Add(new AstapStarDatabase(folder, name, areas, description, ordered));
            }
        }
        return [.. found.OrderByDescending(static d => d.MaxStarsPerSquareDegree ?? 0).ThenByDescending(static d => d.Areas).ThenBy(static d => d.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The stars within <paramref name="radiusDegrees"/> of a point, no fainter than <paramref name="maxMagnitude"/>,
    /// brightest first, with how deep the database reached there.
    /// </summary>
    /// <param name="raHours">The cone's centre, right ascension in hours, at the database's epoch.</param>
    /// <param name="decDegrees">The cone's centre, declination in degrees.</param>
    /// <param name="radiusDegrees">The cone's radius, over 0 and at most 180.</param>
    /// <param name="maxMagnitude">The faintest magnitude to return; the count and the depth are of every star in the cone.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    public async ValueTask<AstapStarField> ReadConeAsync(double raHours, double decDegrees, double radiusDegrees, float maxMagnitude, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(radiusDegrees, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radiusDegrees, 180);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Math.Abs(decDegrees), 90);
        var cone = new Cone(raHours, decDegrees, radiusDegrees);
        var stars = new List<AstapStar>();
        var inCone = 0;
        var faintest = float.NaN;
        foreach (var span in await RingSpansAsync().ConfigureAwait(false))
        {
            if (span.DecMax + RingPaddingDegrees < cone.DecLow || span.DecMin - RingPaddingDegrees > cone.DecHigh)
            {
                continue;
            }
            foreach (var cell in await CellsAsync(span.Ring).ConfigureAwait(false))
            {
                if (cell.DecMax < cone.DecLow || cell.DecMin > cone.DecHigh || !cone.MayHoldRa(cell.RaMin, cell.RaMax))
                {
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(cell.Path, cancellationToken).ConfigureAwait(false);
                Collect(bytes, cone, maxMagnitude, stars, ref inCone, ref faintest);
            }
        }
        stars.Sort(static (a, b) => a.Magnitude.CompareTo(b.Magnitude));
        var area = 2 * Math.PI * (1 - Math.Cos(double.DegreesToRadians(radiusDegrees))) * (180 / Math.PI) * (180 / Math.PI);
        var limited = MaxStarsPerSquareDegree is { } cap && inCone >= 0.9 * cap * area;
        return new AstapStarField([.. stars], inCone, area, faintest, limited);
    }

    /// <summary>Every star of one file's bytes, in file order (brightest section first).</summary>
    /// <exception cref="NotSupportedException">The file's records are not the 5-byte kind.</exception>
    internal static ImmutableArray<AstapStar> ReadFile(ReadOnlySpan<byte> file)
    {
        var stars = ImmutableArray.CreateBuilder<AstapStar>();
        var records = new Records(file);
        while (records.MoveNext())
        {
            stars.Add(new AstapStar(records.RaRaw * RaHoursPerUnit, records.DecRaw * DecDegreesPerUnit, records.Magnitude));
        }
        return stars.ToImmutable();
    }

    private static void Collect(ReadOnlySpan<byte> file, in Cone cone, float maxMagnitude, List<AstapStar> into, ref int inCone, ref float faintest)
    {
        var records = new Records(file);
        while (records.MoveNext())
        {
            var dec = records.DecRaw * DecDegreesPerUnit;
            if (dec < cone.DecLow || dec > cone.DecHigh)
            {
                continue;
            }
            var ra = records.RaRaw * RaHoursPerUnit;
            if (!cone.Holds(ra, dec))
            {
                continue;
            }
            inCone++;
            var magnitude = records.Magnitude;
            if (!(faintest >= magnitude))
            {
                faintest = magnitude;
            }
            if (magnitude <= maxMagnitude)
            {
                into.Add(new AstapStar(ra, dec, magnitude));
            }
        }
    }

    private Task<ImmutableArray<RingSpan>> RingSpansAsync()
    {
        if (Volatile.Read(ref _ringSpans) is { } known)
        {
            return known;
        }
        var made = LoadRingSpansAsync();
        return Interlocked.CompareExchange(ref _ringSpans, made, null) ?? made;
    }

    // A ring's declinations, read off its first cell: every cell of a ring spans the same band.
    private async Task<ImmutableArray<RingSpan>> LoadRingSpansAsync()
    {
        var spans = ImmutableArray.CreateBuilder<RingSpan>(_rings.Length);
        foreach (var (ring, files) in _rings)
        {
            var bounds = Bounds(await File.ReadAllBytesAsync(files[0]).ConfigureAwait(false));
            spans.Add(new RingSpan(ring, bounds.DecMin, bounds.DecMax));
        }
        return spans.MoveToImmutable();
    }

    private Task<ImmutableArray<Cell>> CellsAsync(int ring) => _cells.GetOrAdd(ring, LoadCellsAsync);

    private async Task<ImmutableArray<Cell>> LoadCellsAsync(int ring)
    {
        var files = _rings.First(r => r.Ring == ring).Files;
        var cells = ImmutableArray.CreateBuilder<Cell>(files.Length);
        foreach (var path in files)
        {
            var bounds = Bounds(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            // A ring of one cell is a polar cap and holds every right ascension, whatever its stars happen to span.
            cells.Add(files.Length == 1 ? bounds with { Path = path, RaMin = 0, RaMax = 24 } : bounds with { Path = path });
        }
        return cells.MoveToImmutable();
    }

    private static Cell Bounds(ReadOnlySpan<byte> file)
    {
        int raMin = int.MaxValue, raMax = int.MinValue, decMin = int.MaxValue, decMax = int.MinValue;
        var records = new Records(file);
        while (records.MoveNext())
        {
            raMin = Math.Min(raMin, records.RaRaw);
            raMax = Math.Max(raMax, records.RaRaw);
            decMin = Math.Min(decMin, records.DecRaw);
            decMax = Math.Max(decMax, records.DecRaw);
        }
        return raMin > raMax
            ? new Cell("", 0, 0, 0, 0)
            : new Cell("", raMin * RaHoursPerUnit, raMax * RaHoursPerUnit, decMin * DecDegreesPerUnit, decMax * DecDegreesPerUnit);
    }

    private static string? TryReadDescription(string path)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return file.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) == HeaderLength
            ? Encoding.Latin1.GetString(header[..(HeaderLength - 1)]).TrimEnd(' ', '\0')
            : null;
    }

    // name_RRNN.areas: three characters, an underscore, the ring and the cell as two digits each, the cell count as extension.
    private static bool TryParseName(ReadOnlySpan<char> fileName, out ReadOnlySpan<char> name, out int areas, out int ring)
    {
        name = default;
        areas = ring = 0;
        if (fileName.Length < 10 || fileName[3] != '_' || fileName[8] != '.')
        {
            return false;
        }
        name = fileName[..3];
        return int.TryParse(fileName[4..6], NumberStyles.None, CultureInfo.InvariantCulture, out ring)
            && int.TryParse(fileName[6..8], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(fileName[9..], NumberStyles.None, CultureInfo.InvariantCulture, out areas);
    }

    private readonly record struct RingSpan(int Ring, double DecMin, double DecMax);

    private readonly record struct Cell(string Path, double RaMin, double RaMax, double DecMin, double DecMax);

    /// <summary>A cone of sky, with the box of declination and right ascension that holds it.</summary>
    private readonly struct Cone
    {
        private readonly double _x, _y, _z, _cosRadius, _raHours, _raHalfWidthHours;

        public Cone(double raHours, double decDegrees, double radiusDegrees)
        {
            var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDegrees));
            var (sinRa, cosRa) = Math.SinCos(raHours * Math.PI / 12);
            (_x, _y, _z) = (cosDec * cosRa, cosDec * sinRa, sinDec);
            _cosRadius = Math.Cos(double.DegreesToRadians(radiusDegrees));
            _raHours = raHours;
            DecLow = Math.Max(-90, decDegrees - radiusDegrees);
            DecHigh = Math.Min(90, decDegrees + radiusDegrees);
            // Past a pole the cone holds every right ascension; short of it, the widest reach is asin(sin r / cos dec).
            var reach = Math.Sin(double.DegreesToRadians(radiusDegrees)) / cosDec;
            _raHalfWidthHours = decDegrees + radiusDegrees >= 90 || decDegrees - radiusDegrees <= -90 || radiusDegrees >= 90 || reach >= 1
                ? 12
                : double.RadiansToDegrees(Math.Asin(reach)) / 15;
        }

        public double DecLow { get; }

        public double DecHigh { get; }

        public bool Holds(double raHours, double decDegrees)
        {
            var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDegrees));
            var (sinRa, cosRa) = Math.SinCos(raHours * Math.PI / 12);
            return (_x * cosDec * cosRa) + (_y * cosDec * sinRa) + (_z * sinDec) >= _cosRadius;
        }

        /// <summary>Whether a cell spanning these right ascensions (hours, not wrapping) can hold a star of the cone.</summary>
        public bool MayHoldRa(double raMin, double raMax)
        {
            if (_raHalfWidthHours >= 12)
            {
                return true;
            }
            for (var turn = -24; turn <= 24; turn += 24)
            {
                if (_raHours + turn - _raHalfWidthHours <= raMax && _raHours + turn + _raHalfWidthHours >= raMin)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Walks a file's star records, carrying the declination's high byte and the magnitude of the last section header.</summary>
    private ref struct Records
    {
        private const int RecordSize = 5;
        private readonly ReadOnlySpan<byte> _file;
        private int _next;
        private int _decHigh;
        private bool _inSection;

        public Records(ReadOnlySpan<byte> file)
        {
            if (file.Length < HeaderLength)
            {
                throw new InvalidDataException($"An ASTAP star database file holds a {HeaderLength}-byte header; this one is {file.Length} bytes.");
            }
            // The header's last byte is the record size; a space stands for the 11 bytes of the oldest format.
            var size = file[HeaderLength - 1] == (byte)' ' ? 11 : file[HeaderLength - 1];
            if (size != RecordSize)
            {
                throw new NotSupportedException($"This ASTAP star database has {size}-byte records; only the {RecordSize}-byte kind is read.");
            }
            _file = file;
            _next = HeaderLength;
        }

        public int RaRaw { get; private set; }

        public int DecRaw { get; private set; }

        public float Magnitude { get; private set; }

        public bool MoveNext()
        {
            while (_next + RecordSize <= _file.Length)
            {
                var record = _file.Slice(_next, RecordSize);
                _next += RecordSize;
                if (record[0] == 0xFF && record[1] == 0xFF && record[2] == 0xFF)
                {
                    _decHigh = record[3] - 128;
                    Magnitude = (record[4] - 16) / 10f;
                    _inSection = true;
                }
                else if (_inSection)
                {
                    RaRaw = record[0] | (record[1] << 8) | (record[2] << 16);
                    DecRaw = record[3] | (record[4] << 8) | (_decHigh * 65536);
                    return true;
                }
            }
            return false;
        }
    }
}
