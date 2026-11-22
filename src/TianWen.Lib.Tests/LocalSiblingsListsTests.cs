using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The two lists of sibling projects agree (#560). <c>src/Directory.Build.props</c> switches every in-house library to
/// its sibling SOURCE when all of them are cloned beside this repository (<c>UseLocalSiblings</c>), and
/// <c>open-vs.ps1</c> writes the solution Visual Studio opens, with the same projects in a Siblings folder. A sibling
/// added to the switch and not to the script still builds, so nothing fails: Go To Definition drops into the stale
/// NuGet package instead of the source, and it has drifted before without a sign (seven entries once pointed at a
/// repository that no longer existed, and three were missing).
/// </summary>
public partial class LocalSiblingsListsTests
{
    [GeneratedRegex(@"<UseLocalSiblings Condition=""(?<gate>.*?)"">true</UseLocalSiblings>", RegexOptions.Singleline)]
    private static partial Regex Gate { get; }

    // One sibling of the gate: Exists('$(MSBuildThisFileDirectory)..\..\<repo>\...\<name>.csproj')
    [GeneratedRegex(@"Exists\('\$\(MSBuildThisFileDirectory\)\.\.\\\.\.\\(?<project>[^']+\.csproj)'\)")]
    private static partial Regex GatedProject { get; }

    // One project of the script's Siblings folder: <Project Path="../<repo>/.../<name>.csproj" />
    [GeneratedRegex(@"<Project Path=""\.\./(?<project>[^""]+\.csproj)""")]
    private static partial Regex OpenedProject { get; }

    // A <Project> inside an XML comment is not loaded by Visual Studio, so it must not count as opened.
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment { get; }

    [Fact]
    [Trait("Category", "Invariant")]
    public void EverySiblingTheBuildSwitchesToSourceIsInTheSolutionVisualStudioOpens()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } src)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        var props = File.ReadAllText(Path.Combine(src, "Directory.Build.props"));
        var script = File.ReadAllText(Path.Combine(src, "..", "open-vs.ps1"));

        var gate = Gate.Match(props);
        gate.Success.ShouldBeTrue("Directory.Build.props still sets UseLocalSiblings from one condition");

        MissingFromTheScript(gate.Groups["gate"].Value, script).ShouldBeEmpty(
            "a sibling the build switches to source is missing from open-vs.ps1's Siblings folder: add it there");
    }

    /// <summary>
    /// The comparison itself, on two small texts (#2421): the real script holds no commented-out project, so on the real
    /// files alone the stripping of XML comments could be deleted and nothing would say so.
    /// </summary>
    [Fact]
    public void AProjectTheScriptOnlyHoldsInsideACommentIsMissingFromIt()
    {
        const string Gate = @"'$(UseLocalSiblings)' == ''
      AND Exists('$(MSBuildThisFileDirectory)..\..\One.Lib\src\One.Lib\One.Lib.csproj')
      AND Exists('$(MSBuildThisFileDirectory)..\..\Two.Lib\Two.Lib.csproj')";
        const string Opened = @"  <Folder Name=""/Siblings/"">
    <Project Path=""../One.Lib/src/One.Lib/One.Lib.csproj"" />
    <Project Path=""../Two.Lib/Two.Lib.csproj"" />
    <Project Path=""../Extra.Lib/Extra.Lib.csproj"" />
  </Folder>";

        MissingFromTheScript(Gate, Opened).ShouldBeEmpty("both are opened, and one more than the gate holds is no fault");
        MissingFromTheScript(Gate, Opened.Replace(@"    <Project Path=""../Two.Lib/Two.Lib.csproj"" />", @"    <!-- <Project Path=""../Two.Lib/Two.Lib.csproj"" /> -->"))
            .ShouldBe(["Two.Lib/Two.Lib.csproj"], "a project inside a comment is one Visual Studio does not load");
        MissingFromTheScript(Gate, Opened.Replace(@"    <Project Path=""../Two.Lib/Two.Lib.csproj"" />", ""))
            .ShouldBe(["Two.Lib/Two.Lib.csproj"]);
    }

    /// <summary>The projects of a gate's condition that a script's Siblings folder does not open, as the gate names them
    /// with forward slashes.</summary>
    private static string[] MissingFromTheScript(string gate, string script)
    {
        var gated = GatedProject.Matches(gate).Select(static m => m.Groups["project"].Value.Replace('\\', '/')).ToArray();
        // The premise: every Exists(...) of the gate was read, so a pattern that stopped matching cannot leave this
        // comparing nothing with something.
        gated.Length.ShouldBeGreaterThan(0, "the gate holds at least one sibling");
        gate.Split("Exists(").Length.ShouldBe(gated.Length + 1, "and none of them was missed");

        var opened = OpenedProject.Matches(XmlComment.Replace(script, string.Empty)).Select(static m => m.Groups["project"].Value).ToHashSet(StringComparer.Ordinal);

        return [.. gated.Where(project => !opened.Contains(project))];
    }
}
