using System;
using System.IO;
using System.Text;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// One card read out of a file's first header without opening it as FITS (<see cref="FrameProvenance.TryReadCard"/>),
/// and the walk of a header's cards that it and two other readers are made of.
/// </summary>
public sealed class FitsHeaderWalkTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static string Card(string text) => text.PadRight(80);

    // A header of the given cards and its END, padded to whole blocks, and one block of data after it.
    private string Write(string name, params string[] cards)
    {
        var header = new StringBuilder();
        foreach (var card in cards)
        {
            header.Append(Card(card));
        }
        header.Append(Card("END"));
        header.Append(' ', (2880 - header.Length % 2880) % 2880);
        var path = Path.Combine(_folders.Create("header-walk-").FullName, name);
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes(header.ToString()));
        file.Write(new byte[2880]);
        return path;
    }

    private static string[] Mandatory => ["SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    0"];

    [Theory]
    [InlineData("SRCDGST = 'abc123  '           / the lights it was made of", "SRCDGST", "abc123")]   // a string: unquoted and trimmed
    [InlineData("EXPTIME =                 30.0 / seconds", "EXPTIME", "30.0")]                        // a number: up to its comment
    [InlineData("GAIN    =                  121", "GAIN", "121")]                                      // with no comment
    [InlineData("OBJECT  = 'M 42'", "OBJECT", "M 42")]
    [InlineData("STARLESS= 'Classical'          / how the stars were removed", "STARLESS", "Classical")]
    public void ACardIsReadByItsKeywordAStringWithoutItsQuotesAndAnythingElseUpToItsComment(string card, string keyword, string value)
        => FrameProvenance.TryReadCard(Write("one.fits", [.. Mandatory, card]), keyword).ShouldBe(value);

    [Fact]
    public void ACardInALaterBlockIsFoundAndTheFirstOfTwoIsTheOneRead()
    {
        var cards = new System.Collections.Generic.List<string>(Mandatory);
        for (var i = 0; i < 40; i++)
        {
            cards.Add($"FILL{i,-4}=                    {i % 10}");
        }
        cards.Add("SRCDGST = 'second block'");
        cards.Add("SRCDGST = 'a later one'");

        FrameProvenance.TryReadCard(Write("long.fits", [.. cards]), "SRCDGST").ShouldBe("second block");
    }

    [Fact]
    public void AKeywordTheHeaderDoesNotHoldIsNotFoundAndACardAfterTheEndIsNotRead()
    {
        var path = Write("one.fits", Mandatory);
        FrameProvenance.TryReadCard(path, "SRCDGST").ShouldBeNull();
        // The data block is not the header: a card's text in it is not a card.
        var bytes = File.ReadAllBytes(path);
        Encoding.ASCII.GetBytes(Card("SRCDGST = 'in the data'")).CopyTo(bytes, 2880);
        File.WriteAllBytes(path, bytes);
        FrameProvenance.TryReadCard(path, "SRCDGST").ShouldBeNull();
    }

    // The walk itself: every card before the END is seen once and in order, the END and what follows are not, and the
    // stream is left at the end of the END's block, where the data begins.
    [Fact]
    public void AWalkSeesEachCardBeforeTheEndAndLeavesTheStreamWhereTheDataBegins()
    {
        var cards = new System.Collections.Generic.List<string>(Mandatory);
        for (var i = 0; i < 40; i++)
        {
            cards.Add($"FILL{i,-4}=                    {i % 10}");
        }
        using var stream = File.OpenRead(Write("long.fits", [.. cards]));
        var seen = new System.Collections.Generic.List<string>();

        FitsHeaderBlocks.WalkCards(stream, ref seen, static (ReadOnlySpan<byte> card, ref System.Collections.Generic.List<string> all) =>
        {
            all.Add(Encoding.ASCII.GetString(card).TrimEnd());
            return false;
        }).ShouldBe(FitsHeaderBlocks.Walk.End);

        seen.ShouldBe(cards);
        stream.Position.ShouldBe(2 * 2880, "43 cards and their END take two blocks");
    }

    [Fact]
    public void AWalkEndsWhereItsVisitorSaysAndSaysWhenTheHeaderIsCutOrTooLong()
    {
        var path = Write("one.fits", Mandatory);
        var count = 0;
        using (var stream = File.OpenRead(path))
        {
            FitsHeaderBlocks.WalkCards(stream, ref count, static (ReadOnlySpan<byte> _, ref int n) => ++n == 2).ShouldBe(FitsHeaderBlocks.Walk.Stopped);
            count.ShouldBe(2, "it stopped at the second card and saw no third");
        }

        using (var cut = new MemoryStream(File.ReadAllBytes(path), 0, 200))
        {
            FitsHeaderBlocks.WalkCards(cut, ref count, static (ReadOnlySpan<byte> _, ref int _) => false).ShouldBe(FitsHeaderBlocks.Walk.Truncated);
        }

        var blanks = new byte[3 * 2880];
        blanks.AsSpan().Fill((byte)' ');
        using var endless = new MemoryStream(blanks);
        FitsHeaderBlocks.WalkCards(endless, ref count, static (ReadOnlySpan<byte> _, ref int _) => false, maxBlocks: 2).ShouldBe(FitsHeaderBlocks.Walk.TooLong);
        endless.Position.ShouldBe(2 * 2880, "it read the two blocks it was allowed and no more");
    }

    [Theory]
    [InlineData("NAXIS   =                    2", "NAXIS", true)]
    [InlineData("NAXIS1  =                    4", "NAXIS", false)]   // a longer keyword is another keyword
    [InlineData("NAXIS   =                    2", "NAXIS1", false)]
    [InlineData("SRCDGST = 'abc'", "SRCDGST", true)]
    [InlineData("HISTORY made by a test", "HISTORY", true)]
    public void ACardsKeywordIsItsFirstEightBytesLessThePadding(string card, string keyword, bool isIt)
        => FitsHeaderBlocks.KeywordIs(Encoding.ASCII.GetBytes(Card(card)), keyword).ShouldBe(isIt);

    [Fact]
    public void AFileThatEndsInsideItsHeaderOrIsNoFileAtAllHasNoCard()
    {
        var path = Write("cut.fits", Mandatory);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..200]);
        FrameProvenance.TryReadCard(path, "SRCDGST").ShouldBeNull();
        FrameProvenance.TryReadCard(Path.Combine(Path.GetDirectoryName(path) ?? "", "absent.fits"), "SRCDGST").ShouldBeNull();
    }
}
