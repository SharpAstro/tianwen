using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard (#1760): no vendor <c>InitSdkAsync</c> override closes the SDK. The connect that opened the camera closes it
/// when the init is refused (<c>DeviceDriverBase.ReleaseFailedConnectionAsync</c>, through the SDK gate); the ZWO driver closing
/// as well closed it twice and ended another listing's hold. <c>TestDalCameraDriver</c> returns false without closing, so a
/// behaviour test cannot see a vendor override that does, hence this read of the sources.
/// </summary>
[Trait("Category", "Invariant")]
public partial class VendorInitSdkNeverClosesTests
{
    [GeneratedRegex(@"\bInitSdkAsync\s*\(")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\bClose\w*\s*\(")]
    private static partial Regex CloseCall();

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

    /// <summary>The text of each <c>InitSdkAsync</c> override's body or expression body in <paramref name="text"/>, with its line.</summary>
    internal static (int Line, string Body)[] InitBodies(string text) =>
        [.. Declaration().Matches(text)
            .Where(m => text[Math.Max(0, m.Index - 60)..m.Index].Contains("override", StringComparison.Ordinal))
            .Select(m =>
            {
                var brace = text.IndexOf('{', m.Index);
                var arrow = text.IndexOf("=>", m.Index, StringComparison.Ordinal);
                int end;
                if (arrow >= 0 && (brace < 0 || arrow < brace))
                {
                    end = text.IndexOf(';', arrow);
                }
                else
                {
                    var depth = 0;
                    end = brace;
                    for (; end >= 0 && end < text.Length; end++)
                    {
                        if (text[end] == '{')
                        {
                            depth++;
                        }
                        else if (text[end] == '}' && --depth == 0)
                        {
                            break;
                        }
                    }
                }

                end = end < 0 ? text.Length : Math.Min(end, text.Length);
                return (text.AsSpan(0, m.Index).Count('\n') + 1, text[m.Index..end]);
            })];

    [Fact]
    public void NoVendorInitSdkOverrideClosesTheSdk()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        var offenders = FileEnumeration.EnumerateFiles(root, ".cs", recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("VendorInitSdkNeverClosesTests.cs", StringComparison.Ordinal))
            .SelectMany(f => InitBodies(File.ReadAllText(f))
                .Where(b => CloseCall().IsMatch(b.Body))
                .Select(b => $"{Path.GetRelativePath(root, f)}:{b.Line}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("an InitSdkAsync override that closes: the connect that opened the camera closes it when the init is refused");
    }

    /// <summary>The guard sees a close in a block body and in an expression body, and leaves a clean override alone.</summary>
    [Fact]
    public void TheGuardSpotsAClose()
    {
        CloseCall().IsMatch(InitBodies("protected override ValueTask<bool> InitSdkAsync(CancellationToken ct)\n{\n  sdk.Close(id);\n  return x;\n}")
            .Single().Body).ShouldBeTrue();
        CloseCall().IsMatch(InitBodies("protected override ValueTask<bool> InitSdkAsync(CancellationToken c) => Close(1);")
            .Single().Body).ShouldBeTrue();
        CloseCall().IsMatch(InitBodies("protected override ValueTask<bool> InitSdkAsync(CancellationToken c)\n{\n return ok;\n}\nvoid M() { Close(); }")
            .Single().Body).ShouldBeFalse();
    }
}
