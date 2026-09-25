using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How the node's keeper is handed its command line and environment on Windows, where the client starts it with
/// CreateProcessW to break it away from the client's job (P1 of docs/plans/hardware-in-the-server.md, #917). A socket
/// path with a space in it, which a user name can put there, must arrive as one argument.
/// </summary>
public class DetachedProcessTests
{
    [Fact]
    public void AnArgumentWithASpaceIsQuotedAndAPlainOneIsNot()
    {
        DetachedProcess.CommandLine(@"C:\Program Files\TianWen\tianwen-server.exe", ["--keeper", "--socket", @"C:\Users\Ann Lee\TianWen\node.sock"])
            .ShouldBe(@"""C:\Program Files\TianWen\tianwen-server.exe"" --keeper --socket ""C:\Users\Ann Lee\TianWen\node.sock""");
    }

    [Theory]
    // An empty argument is still an argument.
    [InlineData("", @"""""")]
    // A quote inside is escaped.
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    // Backslashes are literal unless a quote follows them, so a trailing one is doubled before the closing quote.
    [InlineData(@"C:\a dir\", @"""C:\a dir\\""")]
    [InlineData(@"C:\no-space\", @"C:\no-space\")]
    // Backslashes before an escaped quote are doubled, then the quote escaped.
    [InlineData(@"a\""b c", @"""a\\\""b c""")]
    public void AnArgumentIsQuotedAsTheCRuntimeSplitsItBack(string argument, string quoted)
    {
        DetachedProcess.CommandLine("x", [argument]).ShouldBe("x " + quoted);
    }

    [Fact]
    public void TheEnvironmentBlockIsThisProcesssChangedSortedAndDoublyTerminated()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        path.ShouldNotBeNull("the test reads a variable every process has");

        var block = DetachedProcess.EnvironmentBlock(new Dictionary<string, string?>
        {
            ["TIANWEN_TEST_ADDED"] = "yes",
            ["PATH"] = null,
        });

        block.ShouldEndWith("\0\0");
        var entries = block[..^2].Split('\0');
        entries.ShouldContain("TIANWEN_TEST_ADDED=yes");
        entries.ShouldNotContain(entry => entry.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase), "a null value removes the variable");
        var names = entries.Select(static entry => entry[..entry.IndexOf('=', 1)]).ToArray();
        names.ShouldBe(names.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(), "sorted, as Windows expects");
    }
}
