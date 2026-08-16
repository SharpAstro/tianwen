using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpAstro.Lzip;
using TianWen.Lib.Astrometry.Catalogs;

namespace BakeTycho2;

/// <summary>
/// Slices the Tycho-2 catalog into region-aligned lzip members so a browser can fetch the sky it is
/// looking at. See BakeTycho2.csproj for what it emits and docs/plans/web-tycho2.md for why.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Uncompressed bytes per member. 256 KB measured as the knee: +1.4% on the asset, and a wide
    /// view costs 69 files / 11.87 MiB against an unconditional 28.88 MiB today. Smaller members
    /// download less but cannot be coalesced into fewer requests -- byte ranges are unusable on both
    /// GitHub and Cloudflare Pages -- so 64 KB would trade 2.2 MiB for 144 extra round trips on
    /// exactly the view where the user is waiting.
    /// </summary>
    private const int DefaultTargetMemberBytes = 256 * 1024;

    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine(
                "usage: BakeTycho2 <tyc2.bin.lz> <output-dir> [targetMemberBytes]");
            return 2;
        }

        var input = args[0];
        var outputDir = args[1];
        var target = args.Length == 3 ? int.Parse(args[2]) : DefaultTargetMemberBytes;

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"[bake-tyc2] input not found: {input}");
            return 1;
        }

        var sw = Stopwatch.StartNew();

        // The committed asset is the source of truth, not the upstream catalog: the members are a
        // repack of the exact bytes the desktop embeds, so the two encodings cannot drift into
        // disagreeing about the sky.
        var raw = LzipDecoder.Decompress(File.ReadAllBytes(input));
        var regionCount = BinaryPrimitives.ReadInt32LittleEndian(raw);
        Console.WriteLine($"[bake-tyc2] {input}: {raw.Length:N0} raw bytes, {regionCount:N0} GSC regions");

        if (regionCount <= 0 || 4 + (regionCount * 4) > raw.Length)
        {
            Console.Error.WriteLine($"[bake-tyc2] implausible region count {regionCount}; wrong file?");
            return 1;
        }

        var header = raw.AsSpan(0, 4 + (regionCount * 4));
        var (byteBoundary, regionBoundary) = Tycho2MemberManifest.Pack(header, regionCount, raw.Length, target);
        var memberCount = byteBoundary.Length - 1;
        Console.WriteLine($"[bake-tyc2] packed into {memberCount:N0} members at a {target / 1024} KB target");

        var members = new byte[memberCount][];
        Parallel.For(0, memberCount, i =>
        {
            members[i] = LzipEncoder.Compress(raw.AsSpan(byteBoundary[i], byteBoundary[i + 1] - byteBoundary[i]));
        });

        // Verify BEFORE writing: the concatenated members must decode back to the identical bytes.
        // That is the whole safety argument for deriving the web assets from the desktop's -- an
        // unverified repack would be a second source of truth wearing a disguise.
        using (var concatenated = new MemoryStream())
        {
            foreach (var member in members)
            {
                concatenated.Write(member);
            }

            var roundTripped = LzipDecoder.Decompress(concatenated.ToArray());
            if (!roundTripped.AsSpan().SequenceEqual(raw))
            {
                Console.Error.WriteLine("[bake-tyc2] FAILED: members do not decode back to the input");
                return 1;
            }
        }

        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.EnumerateFiles(outputDir, "m*.lz"))
        {
            File.Delete(stale);
        }

        for (var i = 0; i < memberCount; i++)
        {
            File.WriteAllBytes(Path.Combine(outputDir, Tycho2MemberManifest.MemberFileName(i)), members[i]);
        }

        var manifest = Tycho2MemberManifest.Create(regionBoundary, regionCount, raw.Length);
        File.WriteAllBytes(Path.Combine(outputDir, "manifest.bin"), manifest.Write());

        var total = members.Sum(m => (long)m.Length);
        var single = new FileInfo(input).Length;
        Console.WriteLine(
            $"[bake-tyc2] wrote {memberCount:N0} members + manifest to {outputDir}: "
            + $"{total / (1024.0 * 1024.0):F2} MiB vs {single / (1024.0 * 1024.0):F2} MiB single-file "
            + $"({100.0 * total / single - 100.0:+0.0;-0.0}%), verified round-trip, {sw.ElapsedMilliseconds:N0} ms");

        return 0;
    }
}
