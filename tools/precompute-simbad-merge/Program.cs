using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;

namespace PrecomputeSimbadMerge;

// Runs CelestialObjectDB.InitDBAsync with ForceLiveSimbadMergeWithCapture=true so the
// SIMBAD merge phase produces a SimbadMergeSnapshot, then writes it (alongside the input
// hash) to the requested output path as a gzipped blob.
//
// Usage:
//   dotnet run --project tools/precompute-simbad-merge -- --output <path>
//
// The output path is overwritten atomically (temp + rename) so a half-baked snapshot
// can never leak onto disk if the tool crashes mid-write.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var output = ParseOutputArg(args);
        if (output is null)
        {
            Console.Error.WriteLine("usage: precompute-simbad-merge --output <path>");
            return 2;
        }

        var sw = Stopwatch.StartNew();
        var db = new CelestialObjectDB { ForceLiveSimbadMergeWithCapture = true };
        await db.InitDBAsync(cancellationToken: CancellationToken.None);

        if (db.LastSimbadMergeSnapshot is not { } snapshot)
        {
            Console.Error.WriteLine("ERROR: InitDBAsync completed but LastSimbadMergeSnapshot is null. " +
                "Check that ForceLiveSimbadMergeWithCapture is honoured by the build of TianWen.Lib being referenced.");
            return 1;
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var inputHash = SimbadMergeInputHasher.Compute(assembly, manifestNames);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var tmp = output + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                SimbadMergeSnapshotIo.Write(fs, inputHash, snapshot);
            }
            File.Move(tmp, output, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        var sizeBytes = new FileInfo(output).Length;
        Console.Out.WriteLine(
            $"simbad-merge snapshot: {snapshot.Objects.Length} objects + {snapshot.Edges.Length} edges -> {output} ({sizeBytes:N0} bytes) in {sw.Elapsed.TotalSeconds:F1}s");
        DumpInitTimings(db);
        return 0;
    }

    private static string? ParseOutputArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
            if (args[i].StartsWith("--output=", StringComparison.Ordinal))
            {
                return args[i]["--output=".Length..];
            }
        }
        return null;
    }

    private static void DumpInitTimings(CelestialObjectDB db)
    {
        Console.Out.WriteLine($"Init phases ({db.LastInitProcessed:N0} processed, {db.LastInitFailed} failed):");
        foreach (var (phase, elapsed) in db.LastInitPhaseTimings)
        {
            Console.Out.WriteLine($"  {phase,-32} {elapsed.TotalMilliseconds,8:F1} ms");
        }
    }
}
