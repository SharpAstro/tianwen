using System;
using System.IO;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a night's subs land must not depend on where the process was started (P0c item 4 of
/// docs/plans/hardware-in-the-server.md, #788). On Linux, .NET answers the Pictures folder with an EMPTY string
/// when <c>~/Pictures</c> does not exist, unless asked to create it, and an empty folder combined with the app's
/// name is the relative path <c>TianWen</c>: the working directory decided where the lights went, and a spawned
/// server's working directory is whatever its spawner's was. The folder lookup is injected here, since the
/// machine running the test has its own answer.
/// </summary>
public class ImageOutputFolderTests
{
    private static readonly string Pictures = Path.Combine(Path.GetTempPath(), "home", "Pictures");

    private static readonly string Fallback = Path.Combine(Path.GetTempPath(), "TianWen", "Images");

    [Fact]
    public void AMissingPicturesFolderIsCreatedRatherThanTakenRelativeToTheWorkingDirectory()
    {
        // .NET on Linux: an empty answer for a folder that does not exist, unless asked to create it.
        static string GetFolderPath(Environment.SpecialFolder folder, Environment.SpecialFolderOption option)
            => option is Environment.SpecialFolderOption.Create ? Pictures : "";

        var resolved = SpecialFolderHelper.ResolveAppSubFolder(GetFolderPath, Environment.SpecialFolder.MyPictures, "TianWen", Fallback);

        Path.IsPathFullyQualified(resolved).ShouldBeTrue($"'{resolved}' is relative, so the working directory decides where the lights go");
        resolved.ShouldBe(Path.Combine(Pictures, "TianWen"));
    }

    [Fact]
    public void WithNoPicturesFolderAtAllTheOutputFallsBackToAnAbsoluteFolder()
    {
        // A service account with no home has no Pictures folder to create either.
        static string GetFolderPath(Environment.SpecialFolder folder, Environment.SpecialFolderOption option) => "";

        SpecialFolderHelper.ResolveAppSubFolder(GetFolderPath, Environment.SpecialFolder.MyPictures, "TianWen", Fallback).ShouldBe(Fallback);
    }
}
