using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The guard from viewer-layout-engine.md P4: chrome does not do its own text measurement.
/// <para>
/// A <c>MeasureText</c> in a painter is how a hand-laid-out panel starts. The count reached 47 one
/// convenient call at a time, and every one of them was locally reasonable -- which is exactly why a
/// guard is worth more here than another round of cleanup. This is a RATCHET, not a zero: it pins what
/// each file is allowed today and fails on any increase, so the number can only come down.
/// </para>
/// <para>
/// Adding a call is not forbidden, it is a decision: put the measurement on the tree instead, or lower
/// the file's allowance here in the same commit as the work that earns it. A NEW file calling
/// <c>MeasureText</c> fails outright, because a fresh painter has no excuse -- the engine was already
/// there when it was written.
/// </para>
/// </summary>
public class ChromeMeasuresThroughTheEngineTests
{
    /// <summary>
    /// What each file may still call, as of 2026-09-16. These are the escape hatches and the debt: an
    /// overlay label placed against a star's ellipse, the file list's ellipsis budget, a toolbar mark
    /// width. Lower a number when the work lands; never raise one.
    /// </summary>
    private static readonly Dictionary<string, int> Allowance = new(StringComparer.Ordinal)
    {
        ["EquipmentTab.DeviceList.cs"] = 3,
        ["EquipmentTab.ProfilePanel.cs"] = 2,
        ["ImageRendererBase.FileList.cs"] = 3,
        ["ImageRendererBase.Histogram.cs"] = 1,
        ["ImageRendererBase.Overlays.cs"] = 5,
        ["ImageRendererBase.Toolbar.cs"] = 6,
        ["ImageRendererBase.Transport.cs"] = 3,
        ["ImageRendererBase.cs"] = 11,
        ["SkyMapTab.ObjectOverlay.cs"] = 1,
        ["SkyMapTab.cs"] = 1,
        ["VkGuiRenderer.cs"] = 3,
        ["VkPlannerTab.cs"] = 1,
        ["VkSkyMapTab.cs"] = 6,
    };

    private static readonly string[] ChromeProjects =
        ["TianWen.UI.Abstractions", "TianWen.UI.Shared", "TianWen.UI.Gui"];

    /// <summary>
    /// Walks up from the test binary looking for the solution, so this works from a bin directory at any
    /// depth. Returns null when the sources are not beside the binary at all -- a packaged run -- and the
    /// tests below skip rather than fail, because "I cannot see the source" is not a regression.
    /// </summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(candidate)
                && ChromeProjects.All(p => Directory.Exists(Path.Combine(candidate, p))))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static Dictionary<string, int> CountMeasureTextCalls(string sourceRoot)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in ChromeProjects)
        {
            var projectDir = Path.Combine(sourceRoot, project);
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                // Build output under the project dir is not source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var hits = File.ReadLines(file).Count(line => line.Contains("MeasureText", StringComparison.Ordinal));
                if (hits > 0)
                {
                    counts[Path.GetFileName(file)] = hits;
                }
            }
        }

        return counts;
    }

    [Fact]
    public void NoChromeFileMeasuresTextMoreThanItIsAllowedTo()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var counts = CountMeasureTextCalls(root);

        var overBudget = counts
            .Where(kv => Allowance.TryGetValue(kv.Key, out var allowed) && kv.Value > allowed)
            .Select(kv => $"{kv.Key}: {kv.Value} > {Allowance[kv.Key]}")
            .ToArray();

        overBudget.ShouldBeEmpty(
            "a chrome file gained a MeasureText call. Put the measurement on the tree -- the box IS the "
            + "measure -- or lower the allowance in the same commit as the work that earns it.");
    }

    /// <summary>
    /// A file not on the list at all. Stricter than the allowance on purpose: an existing painter has
    /// history, a new one does not, and the engine was already there when it was written.
    /// </summary>
    [Fact]
    public void NoNewChromeFileMeasuresTextAtAll()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var newcomers = CountMeasureTextCalls(root).Keys.Where(f => !Allowance.ContainsKey(f)).ToArray();

        newcomers.ShouldBeEmpty(
            "a new chrome file measures text by hand. Declare the content and let the engine measure it; "
            + "see docs/plans/viewer-layout-engine.md.");
    }

    /// <summary>
    /// The ratchet's other half: an allowance for a file that no longer needs it is debt that has been
    /// PAID, and leaving the number behind lets it be spent again silently.
    /// </summary>
    [Fact]
    public void TheAllowanceListHasNoEntriesThatAreNoLongerEarned()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var counts = CountMeasureTextCalls(root);

        var stale = Allowance
            .Where(kv => !counts.TryGetValue(kv.Key, out var actual) || actual < kv.Value)
            .Select(kv => $"{kv.Key}: allowed {kv.Value}, actually {(counts.TryGetValue(kv.Key, out var a) ? a : 0)}")
            .ToArray();

        stale.ShouldBeEmpty("lower these allowances to what the files now use, so the budget cannot be re-spent");
    }
}
