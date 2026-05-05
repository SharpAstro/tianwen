using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;

namespace PrecomputeHdHipCross;

// Runs CelestialObjectDB.InitDBAsync with ForceLiveHdHipCrossWithCapture=true so the
// hd-hip-cross phase produces a HdHipCrossSnapshot, then writes it (alongside the input
// hash) to the requested output path as a gzipped blob.
//
// Usage:
//   dotnet run --project tools/precompute-hd-hip-cross -- --output <path>
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
            Console.Error.WriteLine("usage: precompute-hd-hip-cross --output <path>");
            return 2;
        }

        var sw = Stopwatch.StartNew();
        var db = new CelestialObjectDB { ForceLiveHdHipCrossWithCapture = true };
        await db.InitDBAsync(CancellationToken.None);

        if (db.LastHdHipCrossSnapshot is not { } snapshot)
        {
            Console.Error.WriteLine("ERROR: InitDBAsync completed but LastHdHipCrossSnapshot is null. " +
                "Check that ForceLiveHdHipCrossWithCapture is honoured by the build of TianWen.Lib being referenced.");
            return 1;
        }

        var assembly = typeof(CelestialObjectDB).Assembly;
        var manifestNames = assembly.GetManifestResourceNames();
        var inputHash = HdHipCrossInputHasher.Compute(assembly, manifestNames);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var tmp = output + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                HdHipCrossSnapshotIo.Write(fs, inputHash, snapshot);
            }
            // Atomic-ish on Windows: File.Move overwrites if the target is on the same volume
            // and Windows supports rename-replace as a single MoveFileEx call. Good enough for
            // a build-time tool; if it ever races with a TianWen.Lib build the target would
            // either be the old snapshot (acceptable, runtime hash-checks) or the new one.
            File.Move(tmp, output, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        var sizeBytes = new FileInfo(output).Length;
        Console.Out.WriteLine(
            $"hd-hip-cross snapshot: {snapshot.HdEntries.Length} HD entries + {snapshot.Edges.Length} edges -> {output} ({sizeBytes:N0} bytes) in {sw.Elapsed.TotalSeconds:F1}s");
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
