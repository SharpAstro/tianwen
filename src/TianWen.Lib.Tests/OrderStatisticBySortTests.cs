using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: nobody sorts a whole buffer to read ONE rank out of it.
///
/// <para>The shape is <c>Array.Sort(x);</c> followed within a few lines by <c>x[k]</c>, and it is
/// what a median, a MAD or a percentile looks like when it is written out by hand. It allocates a
/// copy to protect the caller's array, spends <c>O(n log n)</c> putting the other n - 1 values in an
/// order nothing reads, and, worse than either, it is a private reimplementation of
/// <see cref="Stat.StatisticsHelper"/> that drifts: fourteen such sites existed across six files,
/// and they disagreed about the convention. Some took <c>sorted[n / 2]</c> (the UPPER median on an
/// even count), some averaged the two middle values, some indexed <c>(int)(len * p)</c> and one
/// rounded <c>p * (len - 1)</c> instead. Each is a different number on the same data.</para>
///
/// <para>The replacements are <see cref="Stat.StatisticsHelper.NthSmallest(Span{float}, int)"/>,
/// which is BIT-IDENTICAL to <c>sorted[k]</c> and so preserves whichever convention a call site
/// already had, <c>MedianFast</c> for the averaging convention, and <c>PercentileFast</c> where the
/// truncated rank is wanted. Reach for the one that matches the call site, never the one that reads
/// best: swapping conventions moves the answer silently, which is the trap this test's own
/// clean-up had to avoid at eight separate sites.</para>
///
/// <para>Sorting is still right when the ORDER is what you need. The allowlist below carries such a
/// case and its reason. Adding to it is a decision, and it should be made in a review, not by
/// deleting a red test.</para>
/// </summary>
public class OrderStatisticBySortTests
{
    /// <summary>
    /// Sorts that are genuinely sorts, by file, with the count expected in each. A site here needs
    /// a reason, and the reason must be that something downstream consumes the ORDERING.
    /// </summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        // Sorts the sample KEYS to walk the focus curve in position order: it reads the ends
        // (keys[0], keys[^1]) AND iterates the sequence, so the ordering is the product.
        ["MetricSampleMap.cs"] = 1,
    };

    private static readonly string[] ScannedProjects =
    [
        "TianWen.Lib", "TianWen.AI", "TianWen.AI.Imaging", "TianWen.Cli",
        "TianWen.UI.Abstractions", "TianWen.UI.Shared", "TianWen.UI.Gui", "TianWen.Hosting",
    ];

    /// <summary>How many lines after the sort still count as "reading a rank off it".</summary>
    private const int WindowLines = 4;

    private static readonly Regex SortCall = new(@"Array\.Sort\(\s*(\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Walks up from the test binary looking for the solution folder, so this works from a bin
    /// directory at any depth. Returns null when the sources are not beside the binary at all (a
    /// packaged run), and the test skips rather than fails: "I cannot see the source" is not a
    /// regression.
    /// </summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "TianWen.Lib")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static List<string> FindSortsReadAsRanks(string sourceRoot)
    {
        var found = new List<string>();
        foreach (var project in ScannedProjects)
        {
            var projectDir = Path.Combine(sourceRoot, project);
            if (!Directory.Exists(projectDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                // Build output under a project directory is not source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in SortCall.Matches(lines[i]))
                    {
                        var buffer = m.Groups[1].Value;
                        var indexRead = new Regex($@"\b{Regex.Escape(buffer)}\[", RegexOptions.None);
                        var reads = lines
                            .Skip(i + 1)
                            .Take(WindowLines)
                            .Any(l => indexRead.IsMatch(l));
                        if (reads)
                        {
                            found.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                        }
                    }
                }
            }
        }

        return found;
    }

    [Fact]
    public void NobodySortsAWholeBufferToReadOneRank()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;   // a packaged run has no sources beside the binary; matching ChromeMeasuresThroughTheEngineTests
        }

        var found = FindSortsReadAsRanks(root);
        var byFile = found
            .GroupBy(f => f.Split(':')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var unexpected = byFile
            .Where(kv => !Allowed.TryGetValue(kv.Key, out var allowed) || kv.Value > allowed)
            .Select(kv => $"{kv.Key}: {kv.Value} (allowed {(Allowed.TryGetValue(kv.Key, out var a) ? a : 0)})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        unexpected.ShouldBeEmpty(
            "A full sort feeding a single index read is an order statistic written out by hand. Use "
            + "StatisticsHelper.NthSmallest (bit-identical to sorted[k], so it preserves the call "
            + "site's convention), MedianFast, or PercentileFast. If the ORDER is genuinely the "
            + "product, add the file to Allowed with the reason.\n"
            + string.Join("\n", found.Where(f => unexpected.Any(u => u.StartsWith(f.Split(':')[0], StringComparison.Ordinal)))));
    }

    /// <summary>
    /// The allowlist must not outlive its entries. A stale name here would quietly license a future
    /// file of the same name somewhere else in the tree.
    /// </summary>
    [Fact]
    public void TheAllowlistHasNoStaleEntries()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;   // a packaged run has no sources beside the binary; matching ChromeMeasuresThroughTheEngineTests
        }

        var byFile = FindSortsReadAsRanks(root)
            .GroupBy(f => f.Split(':')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var (file, count) in Allowed)
        {
            byFile.ShouldContainKey(file, $"{file} is allowlisted but no longer sorts-then-indexes; remove the entry");
            byFile[file].ShouldBe(count, $"{file} allowlists {count} but has {byFile.GetValueOrDefault(file)}");
        }
    }
}
