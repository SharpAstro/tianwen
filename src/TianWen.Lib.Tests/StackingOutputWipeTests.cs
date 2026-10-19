using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins which masters a stack run deletes from its output folder before it writes (<c>StackingPipeline.IsStaleMaster</c>).
/// A run with <c>--group-filter</c> restacked the Tarantula into the folder that also held that night's Sculptor and
/// deleted the Sculptor master's FITS (2026-10-19); a filtered run replaces only the groups it stacks.
/// </summary>
public class StackingOutputWipeTests
{
    private const string Sculptor = "SculptorGalaxy_light_120s_-5C_g120";
    private const string Tarantula = "TarantulaNebula_light_120s_-5C_g120";
    private static readonly string[] Both = [Sculptor, Tarantula];

    [Theory]
    [InlineData("master_TarantulaNebula_light_120s_-5C_g120_drizzle.fits")]
    [InlineData("master_TarantulaNebula_light_120s_-5C_g120_drizzle_autocrop.fits")]
    [InlineData("master_TarantulaNebula_light_120s_-5C_g120.fits")]
    public void AFilteredRunReplacesTheGroupItStacks(string file)
        => StackingPipeline.IsStaleMaster(file, Both, new HashSet<string> { Tarantula }, filtered: true).ShouldBeTrue();

    [Theory]
    [InlineData("master_SculptorGalaxy_light_120s_-5C_g120_drizzle.fits")]
    [InlineData("master_SculptorGalaxy_light_120s_-5C_g120_drizzle_autocrop.fits")]
    public void AFilteredRunKeepsTheGroupsItLeavesOut(string file)
        => StackingPipeline.IsStaleMaster(file, Both, new HashSet<string> { Tarantula }, filtered: true).ShouldBeFalse();

    [Fact]
    public void AMasterOfNoGroupHereIsReplacedOnlyByAnUnfilteredRun()
    {
        const string Orphan = "master_HorseheadNebula_light_300s_-10C_g100_drizzle.fits";
        StackingPipeline.IsStaleMaster(Orphan, Both, new HashSet<string>(Both), filtered: false).ShouldBeTrue();
        StackingPipeline.IsStaleMaster(Orphan, Both, new HashSet<string> { Tarantula }, filtered: true).ShouldBeFalse();
    }

    [Fact]
    public void AnUnfilteredRunReplacesEveryGroupsMasters()
    {
        foreach (var slug in Both)
        {
            StackingPipeline.IsStaleMaster($"master_{slug}_drizzle.fits", Both, new HashSet<string>(Both), filtered: false).ShouldBeTrue();
        }
    }

    [Fact]
    public void AGroupIsTheLongestSlugAFileIsNamedAfter()
    {
        // One slug prefixing another (a group with no temperature beside one with) must not claim the other's files.
        const string Short = "Target_light_60s";
        const string Long = "Target_light_60s_-5C";
        string[] slugs = [Short, Long];
        var onlyLong = new HashSet<string> { Long };
        StackingPipeline.IsStaleMaster($"master_{Long}_drizzle.fits", slugs, onlyLong, filtered: true).ShouldBeTrue();
        StackingPipeline.IsStaleMaster($"master_{Short}_drizzle.fits", slugs, onlyLong, filtered: true).ShouldBeFalse();
        StackingPipeline.IsStaleMaster($"master_{Short}.fits", slugs, onlyLong, filtered: true).ShouldBeFalse();
    }
}
