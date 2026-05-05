using System.IO;
using System.IO.Compression;
using System.Linq;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.IO;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Decomposes the cost of the new ASCII-separated <c>.gs.gz</c> catalog reader
/// path so we can see where the merge-phase wall time actually goes. We
/// suspected that <see cref="double.Parse(System.ReadOnlySpan{char}, System.IFormatProvider?)"/>
/// + <see cref="System.Text.Encoding.UTF8"/> string allocation are the
/// dominant per-record costs, but the format-migration plan estimated savings
/// that did not materialise — so the first thing to do before tweaking the
/// format again is to actually measure.
///
/// Variants per catalog (HR, Dobashi, NGC):
///   - WalkOnly   : enumerate records, do not touch fields. Establishes the
///                  baseline cost of the IndexOf-based record split.
///   - StringsOnly: take every field, allocate a managed string for it,
///                  ignore numeric content. Isolates UTF8 -> string cost.
///   - Full       : the actual reader path -- doubles for SIMBAD coords, Half
///                  parses for NGC numerics, plus US-split sub-arrays.
///
/// Run with:
///   dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter *CatalogParse*
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CatalogParseBenchmarks
{
    private byte[] _hrBytes = null!;        // HR: 9 110 records, SIMBAD shape (heaviest at-scale SIMBAD with full Ids)
    private byte[] _dobashiBytes = null!;   // Dobashi: 7 614 records, SIMBAD shape (was the slowest SIMBAD pre-migration)
    private byte[] _ngcBytes = null!;       // NGC: 13 969 records, 15-field NGC shape

    [GlobalSetup]
    public void Setup()
    {
        _hrBytes      = LoadDecompressedGs("HR");
        _dobashiBytes = LoadDecompressedGs("Dobashi");
        _ngcBytes     = LoadDecompressedGs("NGC");
    }

    private static byte[] LoadDecompressedGs(string name)
    {
        var asm = typeof(CelestialObjectDB).Assembly;
        var manifest = asm.GetManifestResourceNames().First(n => n.EndsWith("." + name + ".gs.gz"));
        using var stream = asm.GetManifestResourceStream(manifest)!;
        return DecompressGzip(stream);
    }

    private static byte[] DecompressGzip(Stream src)
    {
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    // --- Decompression cost (bench it once so we know the floor) ---

    [Benchmark]
    public int Decompress_HR()
    {
        var asm = typeof(CelestialObjectDB).Assembly;
        var manifest = asm.GetManifestResourceNames().First(n => n.EndsWith(".HR.gs.gz"));
        using var stream = asm.GetManifestResourceStream(manifest)!;
        return DecompressGzip(stream).Length;
    }

    [Benchmark]
    public int Decompress_NGC()
    {
        var asm = typeof(CelestialObjectDB).Assembly;
        var manifest = asm.GetManifestResourceNames().First(n => n.EndsWith(".NGC.gs.gz"));
        using var stream = asm.GetManifestResourceStream(manifest)!;
        return DecompressGzip(stream).Length;
    }

    // --- HR (SIMBAD shape: MainId | ObjType | Ra | Dec | VMag | BMinusV | Ids[]) ---

    [Benchmark]
    public int HR_WalkOnly()
    {
        var n = 0;
        foreach (var _ in AsciiRecordReader.EnumerateRecords(_hrBytes)) n++;
        return n;
    }

    [Benchmark]
    public int HR_StringsOnly()
    {
        var n = 0;
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(_hrBytes))
        {
            var rec = recMem.Span;
            for (var i = 0; i < 7; i++)
            {
                AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            }
            n++;
        }
        return n;
    }

    [Benchmark]
    public int HR_Full()
    {
        var n = 0;
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(_hrBytes))
        {
            var rec = recMem.Span;
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));         // mainId
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));         // objType
            AsciiRecordReader.ReadDouble(AsciiRecordReader.TakeField(ref rec));         // ra
            AsciiRecordReader.ReadDouble(AsciiRecordReader.TakeField(ref rec));         // dec
            AsciiRecordReader.ReadNullableDouble(AsciiRecordReader.TakeField(ref rec)); // vmag
            AsciiRecordReader.ReadNullableDouble(AsciiRecordReader.TakeField(ref rec)); // bmv
            AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec));    // ids[]
            n++;
        }
        return n;
    }

    // --- Dobashi (same SIMBAD shape, smaller per-record but more records pre-migration) ---

    [Benchmark]
    public int Dobashi_WalkOnly()
    {
        var n = 0;
        foreach (var _ in AsciiRecordReader.EnumerateRecords(_dobashiBytes)) n++;
        return n;
    }

    [Benchmark]
    public int Dobashi_Full()
    {
        var n = 0;
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(_dobashiBytes))
        {
            var rec = recMem.Span;
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadDouble(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadDouble(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadNullableDouble(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadNullableDouble(AsciiRecordReader.TakeField(ref rec));
            AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec));
            n++;
        }
        return n;
    }

    // --- NGC (15 fields; numerics use Half.TryParse on string spans) ---

    [Benchmark]
    public int NGC_WalkOnly()
    {
        var n = 0;
        foreach (var _ in AsciiRecordReader.EnumerateRecords(_ngcBytes)) n++;
        return n;
    }

    [Benchmark]
    public int NGC_StringsOnly()
    {
        var n = 0;
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(_ngcBytes))
        {
            var rec = recMem.Span;
            for (var i = 0; i < 15; i++)
            {
                AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
            }
            n++;
        }
        return n;
    }

    /// <summary>
    /// Mirrors <c>MergeNgcGsData</c>'s field reads only -- no dict mutations,
    /// no <c>HMSToHours</c>/<c>DMSToDegree</c>, no <c>TryGetCleanedUpCatalogName</c>.
    /// Just the cost of producing the 13 strings + 2 string[] arrays the merge
    /// helper consumes.
    /// </summary>
    [Benchmark]
    public int NGC_Full()
    {
        var n = 0;
        foreach (var recMem in AsciiRecordReader.EnumerateRecords(_ngcBytes))
        {
            var rec = recMem.Span;
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // Name
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // Type
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // RA
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // Dec
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // Const
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // V-Mag
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // SurfBr
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // MajAx
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // MinAx
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // PosAng
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // M
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // NGC
            AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));      // IC
            AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec)); // CommonNames
            AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec)); // Identifiers
            n++;
        }
        return n;
    }
}
