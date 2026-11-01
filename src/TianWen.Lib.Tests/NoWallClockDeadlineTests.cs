using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: a deadline is <see cref="TimeProviderExtensions"/>' <c>CancelAfter(delay)</c> on the injected clock, never
/// <c>new CancellationTokenSource(delay)</c> and never <c>CancelAfter</c> on a plain source. Both of those run on the WALL clock
/// whatever clock the code was handed, so a test's fake clock never ends them: the test waits the deadline out for real, or
/// hangs where a fake clock was meant to make it instant (#1952 moved every one in <c>src/</c> onto the injected clock, serial
/// path, Gemini, Canon, Alpaca, guider and hosting alike; this is what keeps it there).
/// <para>
/// What it reads: every <c>.cs</c> and <c>.razor</c> under <c>src/</c> outside the test projects (a project whose csproj names
/// xunit or <c>IsTestProject</c>; a test's real-time safety net is not a device deadline), with the comments blanked so a doc
/// comment that NAMES the forbidden spelling is not a use of it. Three shapes are a hit: <c>new CancellationTokenSource(&lt;any
/// argument&gt;)</c>, the same through a target-typed <c>CancellationTokenSource x = new(&lt;arg&gt;)</c> (a declaration, a field,
/// a property initialiser or an expression-bodied member that says its type), and a <c>.CancelAfter(</c> call whose receiver is
/// not named like a clock (its last identifier ends in <c>Clock</c> or <c>TimeProvider</c>, any case). The receiver is judged
/// by name because a test cannot see types; a plain source that is named like a clock is a lie this guard cannot catch, and a
/// target-typed <c>new(delay)</c> assigned to a variable declared elsewhere is another. A hit is converted to
/// <c>CancelAfter</c> on the clock, never excepted; <see cref="Allowances"/> is the definition of the extension itself and
/// nothing else, and a stale entry fails too.
/// </para>
/// </summary>
public class NoWallClockDeadlineTests
{
    private readonly record struct Hit(string File, int Line, string Code)
    {
        public override string ToString() => $"{File}:{Line}  {Code}";
    }

    private sealed record Allowance(string File, string Code, string Reason);

    /// <summary>
    /// The only places a deadline is built on a source: the two definitions of <c>CancelAfter</c> in
    /// <c>ITimeProvider.cs</c>, which hand the injected clock's own <see cref="TimeProvider"/> to the constructor. An entry is a
    /// file (relative to <c>src</c>, forward slashes) and the exact trimmed line, so an edit to the line asks for a second look.
    /// </summary>
    private static readonly Allowance[] Allowances =
    [
        new("TianWen.Lib/Devices/ITimeProvider.cs",
            "public CancellationTokenSource CancelAfter(TimeSpan delay) => new(delay, timeProvider.System);",
            "the ITimeProvider.CancelAfter definition: the one place a deadline is built, on the injected clock's own TimeProvider"),
        new("TianWen.Lib/Devices/ITimeProvider.cs",
            "public CancellationTokenSource CancelAfter(TimeSpan delay) => new(delay, clock);",
            "the same definition for a clock held as a BCL TimeProvider, so the one spelling covers both"),
    ];

    // Non-backtracking, since a whole file is a long input (as NoCastArrayCloneTests' search is).
    private const RegexOptions Options = RegexOptions.NonBacktracking;

    /// <summary><c>new CancellationTokenSource(</c> followed by anything but the closing parenthesis, qualified or not.</summary>
    private static readonly Regex NewSourceWithArgument = new(
        @"\bnew\s+(?:[\w:]+\.)*CancellationTokenSource\s*\(\s*[^\s)]", Options);

    /// <summary>
    /// A target-typed <c>new(&lt;arg&gt;)</c> where the declared type, or the member's, is a <c>CancellationTokenSource</c>: a local,
    /// a field, a property with an initialiser or a method with an expression body.
    /// </summary>
    private static readonly Regex TargetTypedSourceWithArgument = new(
        @"\bCancellationTokenSource\??\s+[\w.]+\s*(?:\([^()]*\)|\{[^{}]*\})?\s*(?:=|=>)\s*new\s*\(\s*[^\s)]", Options);

    /// <summary>
    /// A call of <c>CancelAfter</c>: the word before the dot, or the closing parenthesis of whatever call produced the receiver.
    /// A declaration of a member of that name has no dot before it and is not a call.
    /// </summary>
    private static readonly Regex CancelAfterCall = new(
        @"(?<recv>\w+|\))\s*\??\.\s*CancelAfter\s*\(", Options);

    /// <summary>A receiver named like a clock: <c>clock</c>, <c>_timeProvider</c>, <c>options.WallClock</c>, <c>TimeProvider</c>.</summary>
    private static readonly Regex ClockReceiver = new(@"(clock|timeprovider)$", Options | RegexOptions.IgnoreCase);

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

    private static IEnumerable<Hit> Scan(string file, string source)
    {
        var code = BlankComments(source);

        Hit At(int index)
        {
            var start = code.LastIndexOf('\n', index) + 1;
            var end = code.IndexOf('\n', index);
            return new Hit(file, code.AsSpan(0, index).Count('\n') + 1, code[start..(end < 0 ? code.Length : end)].Trim());
        }

        foreach (var regex in new[] { NewSourceWithArgument, TargetTypedSourceWithArgument })
        {
            // Matches, not EnumerateMatches: a ref struct enumerator cannot live across the yield.
            foreach (Match match in regex.Matches(code))
            {
                yield return At(match.Index);
            }
        }

        foreach (Match match in CancelAfterCall.Matches(code))
        {
            if (!ClockReceiver.IsMatch(match.Groups["recv"].Value))
            {
                yield return At(match.Index);
            }
        }
    }

    /// <summary>Whether a project's csproj says it is a test project; null when the folder holds no project.</summary>
    private static bool? IsTestProject(string folder)
    {
        var project = Directory.EnumerateFiles(folder, "*.csproj").FirstOrDefault();
        if (project is null)
        {
            return null;
        }

        var text = File.ReadAllText(project);
        return text.Contains("xunit", StringComparison.OrdinalIgnoreCase) || text.Contains("IsTestProject", StringComparison.Ordinal);
    }

    private static (Hit[] Hits, int FilesScanned)? ScanTree()
    {
        if (FindSourceRoot() is not { } root)
        {
            return null;
        }

        var sep = Path.DirectorySeparatorChar;
        var projects = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        bool InTestProject(string file)
        {
            for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
            {
                if (!projects.TryGetValue(dir, out var isTest))
                {
                    projects[dir] = isTest = IsTestProject(dir);
                }

                if (isTest is { } found)
                {
                    return found;
                }
            }

            return false;
        }

        // .razor too: the web host's @code blocks are C# no .cs search sees.
        var files = new[] { ".cs", ".razor" }
            .SelectMany(extension => FileEnumeration.EnumerateFiles(root, extension, recursive: true))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(f => !InTestProject(f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var hits = files
            .SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToArray();

        return (hits, files.Length);
    }

    private static bool IsAllowed(Hit hit) => Allowances.Any(a => a.File == hit.File && a.Code == hit.Code);

    [Fact]
    public void NoDeadlineRunsOnTheWallClock()
    {
        if (Tree.Value is not { } tree)
        {
            return;
        }

        // A guard that scanned nothing is green for the wrong reason.
        tree.FilesScanned.ShouldBeGreaterThan(200, "the scan found next to no source: the root or the test-project filter is wrong");

        var offenders = tree.Hits.Where(h => !IsAllowed(h)).Select(h => h.ToString()).ToArray();

        offenders.ShouldBeEmpty(
            "a deadline on the wall clock; build it with ITimeProvider.CancelAfter(delay) on the injected clock (clock.CancelAfter(delay)), "
            + "never new CancellationTokenSource(delay) nor CancelAfter on a plain source, so a fake clock ends it");
    }

    [Fact]
    public void EveryAllowanceStillNamesALine()
    {
        if (Tree.Value is not { } tree)
        {
            return;
        }

        var stale = Allowances
            .Where(a => !tree.Hits.Any(h => h.File == a.File && h.Code == a.Code))
            .Select(a => $"{a.File}  {a.Code}")
            .ToArray();

        stale.ShouldBeEmpty("an exception for a line that is gone or changed: delete it, or update it to the line as it now reads");
    }

    /// <summary>Every spelling of a wall-clock deadline the guard is for.</summary>
    [Theory]
    [InlineData("using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));")]
    [InlineData("using var cts = new CancellationTokenSource(5000);")]
    [InlineData("var cts = new System.Threading.CancellationTokenSource(delay);")]
    [InlineData("var cts = new global::System.Threading.CancellationTokenSource(delay);")]
    [InlineData("var cts = new CancellationTokenSource(delay, timeProvider.System);")]
    [InlineData("var cts = new CancellationTokenSource(\n        delay);")]
    [InlineData("cts.CancelAfter(TimeSpan.FromSeconds(2));")]
    [InlineData("linked?.CancelAfter(5000);")]
    [InlineData("source\n    .CancelAfter(delay);")]
    [InlineData("GetSource().CancelAfter(delay);")]
    [InlineData("new CancellationTokenSource().CancelAfter(delay);")]
    [InlineData("CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));")]
    [InlineData("private readonly CancellationTokenSource _deadline = new(budget);")]
    [InlineData("private CancellationTokenSource Deadline { get; } = new(budget);")]
    [InlineData("public CancellationTokenSource Start(TimeSpan d) => new(d);")]
    [InlineData("var s = \"http://x\"; cts.CancelAfter(1);")]
    [InlineData("var c = '\"'; cts.CancelAfter(1);")]
    [InlineData("var path = @\"C:\\a\"\"//b\"; cts.CancelAfter(1);")]
    [InlineData("/* a comment */ cts.CancelAfter(1); // and another")]
    public void TheGuardCatchesEverySpellingOfAWallClockDeadline(string code) => Scan("Sample.cs", code).ShouldNotBeEmpty(code);

    /// <summary>What the guard must let through: the clock's own deadline, a source with no delay, and a comment that names the rule.</summary>
    [Theory]
    [InlineData("using var cts = new CancellationTokenSource();")]
    [InlineData("using var cts = timeProvider.CancelAfter(budget);")]
    [InlineData("using var cts = _timeProvider.CancelAfter(budget);")]
    [InlineData("using var cts = clock.CancelAfter(budget);")]
    [InlineData("using var cts = options.WallClock.CancelAfter(budget);")]
    [InlineData("using var cts = TimeProvider.CancelAfter(budget);")]
    [InlineData("deadline = clock?.CancelAfter(budget);")]
    [InlineData("using var cts = timeProvider\n    .CancelAfter(budget);")]
    [InlineData("CancellationTokenSource cts = new();")]
    [InlineData("public CancellationTokenSource Cancellation { get; } = new();")]
    [InlineData("private readonly CancellationTokenSource _stop = new CancellationTokenSource();")]
    [InlineData("var linked = CancellationTokenSource.CreateLinkedTokenSource(token);")]
    [InlineData("public CancellationTokenSource CancelAfter(TimeSpan delay) => Build(delay);")]
    [InlineData("// new CancellationTokenSource(delay) and cts.CancelAfter(x) in a line comment")]
    [InlineData("/// <c>new CancellationTokenSource(delay)</c> or <c>CancelAfter</c> on a plain source")]
    [InlineData("/* cts.CancelAfter(x);\n   new CancellationTokenSource(delay); */ var a = 1;")]
    [InlineData("var s = \"http://x\"; var c = new CancellationTokenSource(); // cts.CancelAfter(1)")]
    [InlineData("var c = '\"'; var t = timeProvider.CancelAfter(d);")]
    public void TheGuardLetsTheClocksOwnDeadlineThrough(string code) => Scan("Sample.cs", code).ShouldBeEmpty(code);
}
