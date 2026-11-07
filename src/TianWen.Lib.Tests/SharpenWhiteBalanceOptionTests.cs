using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>image sharpen --white-balance</c> is the user's statement, so it wins over the WB cards the input carries (#1991): it
/// used to be ignored for such an input with only the file's triple in the log, so an override was lost without a word.
/// </summary>
public sealed class SharpenWhiteBalanceOptionTests
{
    private static readonly ColourCalibration FromFile = new(0.270f, 1f, 0.798f, ColourCalibrationSource.Spcc);

    [Fact]
    public void AnExplicitTripleReplacesTheFilesAndTheNoteNamesBoth()
    {
        var (chosen, note) = ImageSubCommand.ResolveSharpenWhiteBalance(FromFile, (0.38f, 1f, 1.02f));

        chosen.ShouldBe(new ColourCalibration(0.38f, 1f, 1.02f, ColourCalibrationSource.Spcc));
        note.ShouldNotBeNull();
        note.ShouldContain("0.380, 1.000, 1.020");
        note.ShouldContain("0.270, 1.000, 0.798");
        note.ShouldContain("replaces");
    }

    [Fact]
    public void WithNoOptionTheFilesTripleStandsAndNothingIsSaid()
    {
        var (chosen, note) = ImageSubCommand.ResolveSharpenWhiteBalance(FromFile, explicitWb: null);

        chosen.ShouldBe(FromFile);
        note.ShouldBeNull();
    }

    [Fact]
    public void AnExplicitTripleOnAnInputThatStatesNoneIsUsedWithoutANote()
    {
        var (chosen, note) = ImageSubCommand.ResolveSharpenWhiteBalance(fromFile: null, (1.5f, 1f, 0.75f));

        chosen.ShouldBe(new ColourCalibration(1.5f, 1f, 0.75f, ColourCalibrationSource.Spcc));
        note.ShouldBeNull();
    }
}
