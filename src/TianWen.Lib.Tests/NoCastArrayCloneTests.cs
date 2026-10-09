using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: no array is copied by casting what <see cref="Array.Clone"/> returns, whose type is <see cref="object"/>, so the
/// copy is a cast (the owner, 2026-10-09). The cast says nothing about why a copy is made, and most such copies were not needed:
/// a reader takes the array itself or a <see cref="ReadOnlySpan{T}"/>, and a routine that reorders or overwrites its input reads a
/// scratch from <see cref="ArrayPoolHelper"/>. A copy that IS needed is written typed: <c>float[] copy = [.. source];</c> for one
/// dimension, <see cref="ArrayCopyExtensions"/>' <c>Copy()</c> for two. The 99 on main (95 lines) were removed in one pass, each
/// reviewed for whether it was needed at all.
/// </summary>
public class NoCastArrayCloneTests
{
    // An array-typed cast whose statement ends in a clone, a clone cast with "as" or tested with "is", or a clone handed to
    // Unsafe.As as an array. Matched over a whole file, so a cast and its clone on two lines are one statement; the element
    // type may be generic with a comma, a tuple or global::-qualified, and the call may have spaces in it (#1399 found each
    // of those missed while this read one line at a time with a letters-only type). Built from parts, so this file does not
    // match its own search, and non-backtracking, since a whole file is a long input.
    private static readonly string CloneCall = @"\." + "Clone" + @"\s*\(\s*\)";
    private static readonly Regex CastClone = new(
        @"\(\s*[\w.:<>?,\s()]+?(\s*\[[,\s]*\])+\s*\)\s*\(?[^;]*?" + CloneCall
        + "|" + CloneCall + @"\s*(as|is)\s"
        + "|" + @"Unsafe\.As\s*<[^>;]*\[[,\s]*\]\s*>\s*\([^;]*?" + CloneCall,
        RegexOptions.NonBacktracking);

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

    [Fact]
    public void NoArrayIsCopiedThroughACastClone()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        var tools = Path.Combine(root, "..", "tools");
        // .razor too: the web host's @code blocks are C# no .cs search sees.
        var offenders = new[] { root, tools }
            .Where(Directory.Exists)
            .SelectMany(folder => new[] { ".cs", ".razor" }.SelectMany(extension => FileEnumeration.EnumerateFiles(folder, extension, recursive: true)))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(f);
                return CastClone.Matches(text).Select(m =>
                {
                    var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                    var first = m.Value.Split('\n')[0].Trim();
                    return $"{Path.GetRelativePath(root, f)}:{line}  {first}";
                });
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "an array copied through a cast clone; first ask whether the copy is needed (the array or a ReadOnlySpan for a reader, "
            + "a pooled scratch for a routine that reorders its input), else copy it typed: [.. source], or Copy() for two dimensions");
    }

    /// <summary>Every spelling of a cast clone the guard is for, the ones a line-by-line letters-only search missed included
    /// (#1399). Each sample is built from parts so the guard does not find it in this file.</summary>
    [Theory]
    [InlineData("var a = (float[])x." + "Clone" + "();")]
    [InlineData("var a = (float[,])x." + "Clone" + "();")]
    [InlineData("var a = (float[][])x?." + "Clone" + "();")]
    [InlineData("var a = x." + "Clone" + "() as float[];")]
    [InlineData("var a = (Dictionary<int, float>[])x." + "Clone" + "();")]
    [InlineData("var a = ((int, int)[])x." + "Clone" + "();")]
    [InlineData("var a = (global::System.Single[])x." + "Clone" + "();")]
    [InlineData("var a = (float[])\n        source." + "Clone" + "();")]
    [InlineData("var a = (float[])x." + "Clone" + " ();")]
    [InlineData("var a = (float[])x." + "Clone" + "( );")]
    [InlineData("if (x." + "Clone" + "() is float[] copy) { }")]
    [InlineData("var a = Unsafe.As<float[]>(x." + "Clone" + "());")]
    public void TheGuardCatchesEverySpellingOfACastClone(string code) => CastClone.IsMatch(code).ShouldBeTrue(code);

    /// <summary>What the guard must let through: a typed copy, and a clone that is not an array's cast.</summary>
    [Theory]
    [InlineData("float[] copy = [.. source];")]
    [InlineData("var copy = plane.Copy();")]
    [InlineData("object o = x." + "Clone" + "();")]
    [InlineData("var t = typeof(float[]); var n = Next(x);")]
    [InlineData("void M(params float[] values) { }")]
    public void TheGuardLetsATypedCopyThrough(string code) => CastClone.IsMatch(code).ShouldBeFalse(code);
}
