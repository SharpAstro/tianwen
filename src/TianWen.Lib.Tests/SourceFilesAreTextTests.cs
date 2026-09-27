using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source file holds no control byte but tab, line feed and carriage return. A raw NUL is legal C# inside a
/// char literal, and it compiles to the same value as <c>'\0'</c>, but git's text check stops at the first
/// NUL: <c>FileDialogHelper.cs</c> carried three, in the Windows filter string, so every change to it showed
/// as "Binary files differ", no diff could be reviewed and no two branches touching it could be merged.
/// Write the escape instead.
/// </summary>
public class SourceFilesAreTextTests
{
    private static readonly string[] SourceExtensions = [".cs", ".razor", ".csproj", ".props", ".targets"];

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside
    /// the binary (a packaged run), and the test then skips: not seeing the source is not a regression.
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
    public void NoSourceFileHoldsARawControlByte()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        var offenders = FileEnumeration.EnumerateFiles(root, SourceExtensions, recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Select(f => (File: Path.GetRelativePath(root, f), Line: FirstControlByteLine(File.ReadAllBytes(f))))
            .Where(static x => x.Line > 0)
            .Select(static x => $"{x.File}:{x.Line}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a source file holds a raw control byte; write it as an escape ('\\0', '\\u001b', ...) so git keeps treating the file as text");
    }

    /// <summary>The 1-based line of the first byte below 0x20 that is not a tab, line feed or carriage return, else 0.</summary>
    private static int FirstControlByteLine(ReadOnlySpan<byte> bytes)
    {
        var line = 1;
        foreach (var b in bytes)
        {
            if (b == (byte)'\n')
            {
                line++;
            }
            else if (b < 0x20 && b != (byte)'\t' && b != (byte)'\r')
            {
                return line;
            }
        }

        return 0;
    }
}
