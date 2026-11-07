using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: <c>TianWen.Lib</c> waits only through <see cref="TianWen.Lib.Devices.ITimeProvider"/>, never <c>Task.Delay(</c>,
/// <c>Thread.Sleep(</c> or a <c>PeriodicTimer</c> whose period is under a second (#2018, after #2020).
/// <para>
/// Why: on Windows a wait made any other way wakes on the 15.625 ms system timer tick, so a 5 ms poll in a per-frame loop costs
/// three times what it asks, and a guide pulse timed by its wait lasts up to a tick long, which is guiding error. The time
/// provider's <c>SleepAsync</c> is the one place that is made precise (<c>HighResolutionWait</c>, which falls back to
/// <c>Task.Delay</c> past 2.5 s and where the timer cannot be made), and it is also the one a fake clock auto-advances, so a wait
/// routed anywhere else is both a tick late on the real clock and a real wait in a test. A <c>PeriodicTimer</c> is made on a BCL
/// <c>TimeProvider</c> (<c>ITimeProvider.System</c>), which does not take the high-resolution path either.
/// </para>
/// <para>
/// What it reads: every <c>.cs</c> file under <c>src/TianWen.Lib</c> outside <c>bin</c> and <c>obj</c>, with comments blanked so a
/// doc comment that NAMES a spelling is not a use of it. A <c>PeriodicTimer</c> is a hit only when its period is a literal under a
/// second (<c>TimeSpan.FromMilliseconds(250)</c>, <c>FromSeconds(0.5)</c>, <c>FromTicks</c>, <c>FromMicroseconds</c>); one built
/// from a variable is the caller's to size, and a source test cannot see its value (the two there are seconds-long: a dither
/// settle time and the imaging loop's tick). <see cref="Allowances"/> names whole files, today the one that IS the
/// high-resolution wait, and a stale one fails too.
/// </para>
/// </summary>
[Trait("Category", "Invariant")]
public class NoRawDelayInLibTests
{
    private readonly record struct Hit(string File, int Line, string Rule, string Code)
    {
        public override string ToString() => $"{File}:{Line}  [{Rule}] {Code}";
    }

    private sealed record Allowance(string File, string Reason);

    /// <summary>
    /// The only file that may wait another way: <c>HighResolutionWait</c> is what <c>SystemTimeProvider.SleepAsync</c> waits with, and
    /// its <c>Task.Delay</c> is the fallback for a wait past its precise range and for a system with no high-resolution timer.
    /// </summary>
    private static readonly Allowance[] Allowances =
    [
        new("TianWen.Lib/Devices/HighResolutionWait.cs",
            "the time provider's own wait: a Task.Delay for a wait too long to need the high-resolution timer, and where it cannot be made"),
    ];

    // Non-backtracking, since a whole file is a long input (as NoWallClockDeadlineTests' search is).
    private const RegexOptions Options = RegexOptions.NonBacktracking;

    private static readonly Regex TaskDelay = new(@"\bTask\s*\.\s*Delay\s*\(", Options);

    private static readonly Regex ThreadSleep = new(@"\bThread\s*\.\s*Sleep\s*\(", Options);

    /// <summary><c>new PeriodicTimer(</c>, qualified or not; the argument list is read from there.</summary>
    private static readonly Regex NewPeriodicTimer = new(@"\bnew\s+(?:[\w:]+\.)*PeriodicTimer\s*\(", Options);

    /// <summary>A target-typed <c>PeriodicTimer t = new(</c>: a local, a field or a property that says its type.</summary>
    private static readonly Regex TargetTypedPeriodicTimer = new(@"\bPeriodicTimer\??\s+[\w.]+\s*=\s*new\s*\(", Options);

    /// <summary>A literal period, as <c>TimeSpan.FromX(&lt;number&gt;)</c>: the unit and the number, for the sub-second test.</summary>
    private static readonly Regex LiteralPeriod = new(
        @"^\s*(?:global::)?(?:System\.)?TimeSpan\s*\.\s*(?<unit>FromMilliseconds|FromSeconds|FromTicks|FromMicroseconds)\s*\(\s*(?<n>\d[\d_]*(?:\.\d+)?)\s*[dDfFmM]?\s*\)\s*$",
        Options);

    private static readonly Lazy<(Hit[] Hits, int FilesScanned)?> Tree = new(ScanTree);

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside the binary (a packaged
    /// run), and the test then skips: not seeing the source is not a regression.
    /// </summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (File.Exists(Path.Combine(candidate, "TianWen.slnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>The same source with every comment turned to spaces, newlines kept so a position still names its line.</summary>
    private static string BlankComments(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
            if (chars[i] == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                i = Blank(chars, i, end < 0 ? chars.Length : end);
            }
            else if (chars[i] == '/' && next == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(chars, i, close < 0 ? chars.Length : close + 2);
            }
            else if (chars[i] == '"')
            {
                i = EndOfString(text, i);
            }
            else if (chars[i] == '\'')
            {
                i = EndOfChar(text, i);
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    private static int Blank(char[] chars, int from, int to)
    {
        for (var k = from; k < to; k++)
        {
            if (chars[k] is not ('\n' or '\r'))
            {
                chars[k] = ' ';
            }
        }

        return to;
    }

    /// <summary>
    /// Where a string literal that opens at <paramref name="open"/> ends, so a <c>//</c> inside it is not taken for a comment. The
    /// literal itself is left as written: a mistake here can only leave a comment visible (a loud hit), never hide code.
    /// </summary>
    private static int EndOfString(string text, int open)
    {
        var verbatim = open > 0 && (text[open - 1] == '@' || (open > 1 && text[open - 1] == '$' && text[open - 2] == '@'));
        if (verbatim)
        {
            for (var j = open + 1; j < text.Length; j++)
            {
                if (text[j] == '"')
                {
                    if (j + 1 < text.Length && text[j + 1] == '"')
                    {
                        j++;
                        continue;
                    }

                    return j + 1;
                }
            }

            return text.Length;
        }

        var quotes = 0;
        while (open + quotes < text.Length && text[open + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // A raw string literal: as many quotes close it as opened it.
            var close = text.IndexOf(new string('"', quotes), open + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }

        for (var j = open + 1; j < text.Length; j++)
        {
            switch (text[j])
            {
                case '\\':
                    j++;
                    break;
                case '"':
                    return j + 1;
                case '\n':
                    // Unterminated: resume at the line's end rather than swallow the file.
                    return j;
            }
        }

        return text.Length;
    }

    /// <summary>Where a character literal that opens at <paramref name="open"/> ends: <c>'"'</c> must not open a string.</summary>
    private static int EndOfChar(string text, int open)
    {
        if (open + 2 >= text.Length)
        {
            return open + 1;
        }

        var close = text[open + 1] == '\\' ? text.IndexOf('\'', open + 3) : text[open + 2] == '\'' ? open + 2 : -1;
        return close < 0 || close - open > 12 ? open + 1 : close + 1;
    }

    /// <summary>
    /// The text of the first argument of the call whose opening parenthesis is at <paramref name="open"/>: up to the first comma
    /// or the closing parenthesis outside any nested parenthesis. Null when the text ends first.
    /// </summary>
    private static string? FirstArgument(string code, int open)
    {
        var depth = 0;
        for (var j = open; j < code.Length; j++)
        {
            switch (code[j])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return code[(open + 1)..j];
                    }

                    break;
                case ',' when depth == 1:
                    return code[(open + 1)..j];
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="period"/> is a literal <c>TimeSpan.FromX(n)</c> that is under a second.</summary>
    private static bool IsSubSecondLiteral(string period)
    {
        if (LiteralPeriod.Match(period) is not { Success: true } match
            || !double.TryParse(match.Groups["n"].Value.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }

        return match.Groups["unit"].Value switch
        {
            "FromMilliseconds" => n < 1_000,
            "FromSeconds" => n < 1,
            "FromMicroseconds" => n < 1_000_000,
            _ => n < TimeSpan.TicksPerSecond,
        };
    }

    private static IEnumerable<Hit> Scan(string file, string source)
    {
        var code = BlankComments(source);

        Hit At(int index, string rule)
        {
            var start = code.LastIndexOf('\n', index) + 1;
            var end = code.IndexOf('\n', index);
            return new Hit(file, code.AsSpan(0, index).Count('\n') + 1, rule, code[start..(end < 0 ? code.Length : end)].Trim());
        }

        // Matches, not EnumerateMatches: a ref struct enumerator cannot live across the yield.
        foreach (Match match in TaskDelay.Matches(code))
        {
            yield return At(match.Index, "Task.Delay");
        }

        foreach (Match match in ThreadSleep.Matches(code))
        {
            yield return At(match.Index, "Thread.Sleep");
        }

        foreach (var regex in new[] { NewPeriodicTimer, TargetTypedPeriodicTimer })
        {
            foreach (Match match in regex.Matches(code))
            {
                // The opening parenthesis is the match's last character.
                if (FirstArgument(code, match.Index + match.Length - 1) is { } period && IsSubSecondLiteral(period))
                {
                    yield return At(match.Index, "PeriodicTimer under 1 s");
                }
            }
        }
    }

    private static (Hit[] Hits, int FilesScanned)? ScanTree()
    {
        if (FindSourceRoot() is not { } root)
        {
            return null;
        }

        var sep = Path.DirectorySeparatorChar;
        var files = FileEnumeration.EnumerateFiles(Path.Combine(root, "TianWen.Lib"), ".cs", recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var hits = files
            .SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToArray();

        return (hits, files.Length);
    }

    private static bool IsAllowed(Hit hit) => Allowances.Any(a => a.File == hit.File);

    [Fact]
    public void TianWenLibWaitsOnlyThroughTheTimeProvider()
    {
        if (Tree.Value is not { } tree)
        {
            return;
        }

        // A guard that scanned nothing is green for the wrong reason.
        tree.FilesScanned.ShouldBeGreaterThan(300, "the scan found next to no source: the root or the folder is wrong");

        var offenders = tree.Hits.Where(h => !IsAllowed(h)).Select(h => h.ToString()).ToArray();

        offenders.ShouldBeEmpty(
            "a wait that does not go through ITimeProvider: on Windows it ends on a 15.6 ms timer tick, and a fake clock never ends it; "
            + "wait with timeProvider.SleepAsync(duration, token), whose short waits are precise (HighResolutionWait), "
            + "and for a periodic tick take a period of a second or more");
    }

    [Fact]
    public void EveryAllowanceStillNamesAFileThatWaitsItsOwnWay()
    {
        if (Tree.Value is not { } tree)
        {
            return;
        }

        var stale = Allowances
            .Where(a => !tree.Hits.Any(h => h.File == a.File))
            .Select(a => $"{a.File}  {a.Reason}")
            .ToArray();

        stale.ShouldBeEmpty("an exception for a file that no longer waits its own way: delete it");
    }

    /// <summary>Every spelling of a raw wait the guard is for.</summary>
    [Theory]
    [InlineData("await Task.Delay(5, ct);")]
    [InlineData("await Task.Delay(TimeSpan.FromMilliseconds(5), ct);")]
    [InlineData("await System.Threading.Tasks.Task.Delay(delay, token);")]
    [InlineData("await global::System.Threading.Tasks.Task.Delay(delay, token);")]
    [InlineData("await Task\n    .Delay(delay);")]
    [InlineData("return Task.Delay (delay, ct);")]
    [InlineData("Thread.Sleep(1);")]
    [InlineData("System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(5));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(250));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(250), timeProvider.System);")]
    [InlineData("using var ticker = new System.Threading.PeriodicTimer(TimeSpan.FromMilliseconds(999));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(0.5));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromMicroseconds(500_000));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromTicks(5_000_000));")]
    [InlineData("PeriodicTimer ticker = new(TimeSpan.FromMilliseconds(100));")]
    [InlineData("private readonly PeriodicTimer _ticker = new(TimeSpan.FromMilliseconds(100), clock);")]
    [InlineData("var s = \"http://x\"; await Task.Delay(1);")]
    [InlineData("var c = '\"'; Thread.Sleep(1);")]
    [InlineData("/* a comment */ await Task.Delay(1); // and another")]
    public void TheGuardCatchesEverySpellingOfARawWait(string code) => Scan("Sample.cs", code).ShouldNotBeEmpty(code);

    /// <summary>What the guard must let through: the clock's own wait, a period of a second or more, a variable period, and a comment that names the rule.</summary>
    [Theory]
    [InlineData("await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(5), ct);")]
    [InlineData("await _timeProvider.SleepAsync(delay, token);")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(1000), _timeProvider.System);")]
    [InlineData("using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(30), TimeProvider.System);")]
    [InlineData("using var ticker = new PeriodicTimer(tickDuration, _timeProvider.System);")]
    [InlineData("using var ticker = new PeriodicTimer(settleTime, TimeProvider.System);")]
    [InlineData("PeriodicTimer ticker = new(period);")]
    [InlineData("var wait = Delay(5);")]
    [InlineData("var wait = WaitTask.Delay(5);")]
    [InlineData("// await Task.Delay(5, ct); Thread.Sleep(1); new PeriodicTimer(TimeSpan.FromMilliseconds(5))")]
    [InlineData("/// <see cref=\"Task.Delay(TimeSpan, CancellationToken)\"/> and <c>Thread.Sleep(1)</c> in a doc comment")]
    [InlineData("/* Task.Delay(1);\n   Thread.Sleep(1); */ var a = 1;")]
    [InlineData("var s = \"http://x\"; var t = timeProvider.SleepAsync(d); // Task.Delay(1)")]
    public void TheGuardLetsTheClocksOwnWaitThrough(string code) => Scan("Sample.cs", code).ShouldBeEmpty(code);
}
