using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// This edits irreplaceable data in place, so the assertions are about BYTES, not behaviour.
    /// Every test that writes hashes the payload before and after and demands they be identical.
    /// </summary>
    [Collection("Imaging")]
    public class FitsHeaderEditorTests
    {
        private const int Block = FitsHeaderEditor.BlockSize;
        private const int Card = FitsHeaderEditor.CardSize;

        private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "TianWen.HeaderEditorTests", name ?? "unnamed", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Builds a FITS file by hand so the test owns every byte: a primary header of
        /// <paramref name="extraCards"/> plus the mandatory structural cards, then a pseudo-random
        /// but deterministic payload.</summary>
        private static (string Path, byte[] Payload) WriteFits(
            string dir, string name, IEnumerable<string> extraCards, int payloadBytes = Block * 3)
        {
            var cards = new List<string>
            {
                "SIMPLE  =                    T / C# FITS",
                "BITPIX  =                   16",
                "NAXIS   =                    2 / Dimensionality",
                "NAXIS1  =                   40",
                "NAXIS2  =                   36",
                "BZERO   =                32768",
            };
            cards.AddRange(extraCards);

            var headerBlocks = (cards.Count + 1 + 35) / 36;
            var header = new byte[headerBlocks * Block];
            header.AsSpan().Fill((byte)' ');
            for (var i = 0; i < cards.Count; i++)
            {
                Encoding.ASCII.GetBytes(cards[i].PadRight(Card), header.AsSpan(i * Card, Card));
            }
            Encoding.ASCII.GetBytes("END".PadRight(Card), header.AsSpan(cards.Count * Card, Card));

            var payload = new byte[payloadBytes];
            // Deterministic, non-trivial content: a run of zeros would hide a truncation.
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 31 + 7) & 0xFF);
            }

            var path = Path.Combine(dir, name);
            using (var fs = File.Create(path))
            {
                fs.Write(header);
                fs.Write(payload);
            }
            return (path, payload);
        }

        private static byte[] Sha(ReadOnlySpan<byte> data) => SHA256.HashData(data);

        /// <summary>The file's bytes after its primary header, i.e. everything that must survive
        /// untouched. Re-derived from the file on disk rather than assumed.</summary>
        private static byte[] PayloadOf(string path)
        {
            var all = File.ReadAllBytes(path);
            for (var b = 0; b < 32; b++)
            {
                for (var offset = b * Block; offset < (b + 1) * Block; offset += Card)
                {
                    var card = Encoding.ASCII.GetString(all, offset, Card);
                    if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].AsSpan().Trim().IsEmpty)
                    {
                        return all[((b + 1) * Block)..];
                    }
                }
            }
            throw new InvalidOperationException("no END card");
        }

        private static string? HeaderValue(string path, string keyword)
        {
            var all = File.ReadAllBytes(path);
            var cards = new List<string>();
            for (var offset = 0; offset + Card <= all.Length; offset += Card)
            {
                var card = Encoding.ASCII.GetString(all, offset, Card);
                if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].AsSpan().Trim().IsEmpty)
                {
                    break;
                }
                cards.Add(card);
            }
            return FitsHeaderEditor.CardValue(cards, keyword);
        }

        [Fact]
        public async Task GivenAFrameWithNoFilter_WhenTagging_ThenTheCardIsAddedAndEveryPayloadByteSurvives()
        {
            var dir = CreateTempDir();
            var (path, payload) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'              / Type of exposure"]);
            var before = Sha(payload);

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Optolong L-Ultimate 3nm", "Filter name", apply: true,
                cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Tagged);
            HeaderValue(path, "FILTER").ShouldBe("Optolong L-Ultimate 3nm");
            Sha(PayloadOf(path)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenAHeaderThatOverflowsIntoANewBlock_WhenTagging_ThenThePayloadStillSurvives()
        {
            // The dangerous case: the added card pushes END past the block boundary, so every byte
            // after the header shifts by 2880. Nothing may be lost or duplicated in the move.
            var dir = CreateTempDir();
            // 6 structural cards + 29 fillers + END = exactly 36 cards = exactly one block.
            var filler = Enumerable.Range(0, 29).Select(i => $"FILLER{i:D2}= {i,20} / padding to the block edge");
            var (path, payload) = WriteFits(dir, "full.fits", [.. filler]);
            var headerBefore = File.ReadAllBytes(path).Length - payload.Length;
            headerBefore.ShouldBe(Block, "the fixture must start exactly one block full");
            var before = Sha(payload);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            var payloadAfter = PayloadOf(path);
            (File.ReadAllBytes(path).Length - payloadAfter.Length).ShouldBe(Block * 2, "header must have grown by one block");
            Sha(payloadAfter).ShouldBe(before);
            HeaderValue(path, "FILTER").ShouldBe("Ha");
        }

        [Fact]
        public async Task GivenEveryOtherCard_WhenTagging_ThenAllOfThemSurviveUnchangedAndInOrder()
        {
            // The exact failure mode that rules out Image.WriteToFitsFile: cards it does not model
            // are simply gone. Here nothing may be dropped, reordered or reworded.
            var dir = CreateTempDir();
            string[] original =
            [
                "IMAGETYP= 'LIGHT'              / Type of exposure",
                "AIRMASS =     1.08949280569105 / Airmass at frame center (Gueymard 1993)",
                "DATE-AVG= '2026-02-20T14:13:37.0716946' / Averaged midpoint time (UTC)",
                "XBAYROFF=                    0 / Bayer pattern X axis offset",
                "OBSERVER= 'Sebastian Godelet'  / Observer name",
                "FOCTEMP =     25.3899993896484 / [degC] Focuser temperature",
                "USBLIMIT=                   40 / Camera-specific USB setting",
            ];
            var (path, _) = WriteFits(dir, "rich.fits", original);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "L-eXtreme", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            var all = File.ReadAllBytes(path);
            var cards = new List<string>();
            for (var offset = 0; offset + Card <= all.Length; offset += Card)
            {
                var card = Encoding.ASCII.GetString(all, offset, Card);
                if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].AsSpan().Trim().IsEmpty) break;
                cards.Add(card.TrimEnd());
            }
            foreach (var expected in original)
            {
                cards.ShouldContain(expected);
            }
            // Appended, so every pre-existing card keeps its index.
            cards[^1].ShouldStartWith("FILTER  =");
        }

        [Fact]
        public async Task GivenAFrameThatAlreadyStatesItsFilter_WhenTagging_ThenTheFileIsNotTouched()
        {
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits",
                ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'                 / Filter name"]);
            var before = Sha(File.ReadAllBytes(path));

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "OIII", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.AlreadyPresent);
            result.ExistingValue.ShouldBe("Ha");
            Sha(File.ReadAllBytes(path)).ShouldBe(before, "the whole file, header included, must be byte-identical");
        }

        [Fact]
        public async Task GivenOverwriteRequested_WhenTagging_ThenTheExistingCardIsReplacedInPlace()
        {
            var dir = CreateTempDir();
            var (path, payload) = WriteFits(dir, "l1.fits",
                ["IMAGETYP= 'LIGHT'", "FILTER  = 'Ha'", "OBSERVER= 'Sebastian Godelet'"]);
            var before = Sha(payload);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "OIII", overwriteExisting: true, apply: true,
                cancellationToken: TestContext.Current.CancellationToken);

            HeaderValue(path, "FILTER").ShouldBe("OIII");
            HeaderValue(path, "OBSERVER").ShouldBe("Sebastian Godelet");
            Sha(PayloadOf(path)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenADryRun_WhenTagging_ThenNothingIsWritten()
        {
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"]);
            var before = Sha(File.ReadAllBytes(path));

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Tagged);
            result.Detail.ShouldBe("dry run");
            Sha(File.ReadAllBytes(path)).ShouldBe(before);
            Directory.GetFiles(dir).ShouldHaveSingleItem("no temp or backup file may be left behind");
        }

        [Fact]
        public async Task GivenAFrameTypeOutsideTheAllowedSet_WhenTagging_ThenItIsSkipped()
        {
            // An archive folder holds bad-pixel maps and master darks next to the subs; a filter
            // means nothing on those and a blanket tag must not stamp them.
            var dir = CreateTempDir();
            var (bpm, _) = WriteFits(dir, "bpm.fits", ["IMAGETYP= 'BADPIXELMAP'        / Type of frame"]);
            var before = Sha(File.ReadAllBytes(bpm));

            var result = await FitsHeaderEditor.SetStringCardAsync(
                bpm, "FILTER", "Ha", allowedFrameTypes: new HashSet<FrameType> { FrameType.Light, FrameType.Flat },
                apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.FrameTypeExcluded);
            Sha(File.ReadAllBytes(bpm)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenANonFitsFile_WhenTagging_ThenItIsReportedUnreadableAndLeftAlone()
        {
            var dir = CreateTempDir();
            var path = Path.Combine(dir, "notfits.fits");
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0xAB, Block * 2)]);
            var before = Sha(File.ReadAllBytes(path));

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Unreadable);
            Sha(File.ReadAllBytes(path)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenATaggedFrame_WhenOurOwnReaderLoadsIt_ThenItResolvesTheFilter()
        {
            // Closing the loop: a stamped card must be indistinguishable from a recorded one to the
            // same code path the session key uses.
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"], payloadBytes: 40 * 36 * 2);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Filter.IdentityKey.ShouldBe("HydrogenAlpha");
            info.Meta.Filter.RawName.ShouldBe("Ha");
        }

        [Fact]
        public async Task GivenNoTempOrBackupSurvives_WhenTagging_ThenTheDirectoryHoldsOnlyTheFrame()
        {
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"]);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            Directory.GetFiles(dir).ShouldHaveSingleItem().ShouldBe(path);
        }

        [Fact]
        public async Task GivenOneCardOfSlackInTheBlock_WhenTagging_ThenTheFileLengthIsUnchanged()
        {
            // The complement of the overflow test, and the common case: 6 structural + 28 fillers
            // + FILTER + END = 36, exactly filling the block with no shift at all.
            var dir = CreateTempDir();
            var filler = Enumerable.Range(0, 28).Select(i => $"FILLER{i:D2}= {i,20} / padding");
            var (path, payload) = WriteFits(dir, "snug.fits", [.. filler]);
            var lengthBefore = new FileInfo(path).Length;
            var before = Sha(payload);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            new FileInfo(path).Length.ShouldBe(lengthBefore, "the card fit the existing block");
            Sha(PayloadOf(path)).ShouldBe(before);
            HeaderValue(path, "FILTER").ShouldBe("Ha");
        }

        [Fact]
        public async Task GivenAFullHeader_WhenReplacingAnExistingCard_ThenNoBlockIsAdded()
        {
            // Replacement cannot overflow: the card count does not change. Worth pinning, because
            // the append path and the replace path share the same serialiser.
            var dir = CreateTempDir();
            var filler = Enumerable.Range(0, 28).Select(i => $"FILLER{i:D2}= {i,20} / padding");
            var (path, payload) = WriteFits(dir, "full.fits", [.. filler, "FILTER  = 'Ha'"]);
            var lengthBefore = new FileInfo(path).Length;
            var before = Sha(payload);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "OIII", overwriteExisting: true, apply: true,
                cancellationToken: TestContext.Current.CancellationToken);

            new FileInfo(path).Length.ShouldBe(lengthBefore);
            Sha(PayloadOf(path)).ShouldBe(before);
            HeaderValue(path, "FILTER").ShouldBe("OIII");
        }

        [Fact]
        public async Task GivenAnExtensionAfterTheData_WhenTheHeaderOverflows_ThenTheExtensionShiftsIntact()
        {
            // FITS offsets are sequential, never absolute, so shifting everything by one block is
            // correct rather than merely tolerable. This proves a trailing HDU survives the move.
            var dir = CreateTempDir();
            var filler = Enumerable.Range(0, 29).Select(i => $"FILLER{i:D2}= {i,20} / padding");
            var (path, payload) = WriteFits(dir, "ext.fits", [.. filler]);

            // Append a second HDU: its own header block plus a data block.
            var extHeader = new byte[Block];
            extHeader.AsSpan().Fill((byte)' ');
            Encoding.ASCII.GetBytes("XTENSION= 'IMAGE   '".PadRight(Card), extHeader.AsSpan(0, Card));
            Encoding.ASCII.GetBytes("END".PadRight(Card), extHeader.AsSpan(Card, Card));
            var extData = new byte[Block];
            for (var i = 0; i < extData.Length; i++) extData[i] = (byte)(255 - (i & 0xFF));
            using (var fs = new FileStream(path, FileMode.Append))
            {
                fs.Write(extHeader);
                fs.Write(extData);
            }
            var tailBefore = Sha([.. payload, .. extHeader, .. extData]);

            await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            Sha(PayloadOf(path)).ShouldBe(tailBefore, "data AND the trailing extension must move together, byte for byte");
        }

        [Fact]
        public async Task GivenAHeaderOnlyFileWithNoData_WhenTagging_ThenItStillWorks()
        {
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "empty.fits", ["IMAGETYP= 'LIGHT'"], payloadBytes: 0);

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Tagged);
            HeaderValue(path, "FILTER").ShouldBe("Ha");
            PayloadOf(path).Length.ShouldBe(0);
        }

        [Fact]
        public async Task GivenAHeaderLongerThanWeWillWalk_WhenTagging_ThenItIsRefusedAndUntouched()
        {
            // Better to decline an unfamiliar file than to rewrite one we only half understand.
            var dir = CreateTempDir();
            var filler = Enumerable.Range(0, 36 * 40).Select(i => $"F{i:D7}= {i,20} / padding");
            var (path, _) = WriteFits(dir, "huge.fits", [.. filler]);
            var before = Sha(File.ReadAllBytes(path));

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Ha", apply: true, cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Unreadable);
            Sha(File.ReadAllBytes(path)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenANonAsciiValue_WhenTagging_ThenItThrowsBeforeOpeningTheFile()
        {
            // Encoding.ASCII substitutes '?' silently, so "Hα" would be stamped as "H?" into a
            // file that cannot be restored. Our own Filter.ShortName spells it with the Greek
            // letter, which is exactly how someone would come to type it.
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"]);
            var before = Sha(File.ReadAllBytes(path));

            await Should.ThrowAsync<ArgumentException>(async () => await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Hα 3nm", apply: true, cancellationToken: TestContext.Current.CancellationToken));

            Sha(File.ReadAllBytes(path)).ShouldBe(before);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr attributes);

        [Fact]
        public async Task GivenAHardLinkedFrame_WhenTagging_ThenItIsRefusedAndNeitherNameChanges()
        {
            // The real archive de-duplicates with hard links (a Vela panel and its dated filing are
            // one file under two names). File.Replace re-points ONE directory entry, so tagging
            // would leave the sibling on the old header and split one night into two sessions.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard-link count is only probed on Windows.");
            var dir = CreateTempDir();
            var (path, _) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"]);
            var sibling = Path.Combine(dir, "same-frame-other-name.fits");
            Assert.SkipUnless(CreateHardLink(sibling, path, IntPtr.Zero), "Could not create a hard link on this volume.");
            var before = Sha(File.ReadAllBytes(path));

            var dryRun = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Optolong L-Ultimate 3nm", apply: false,
                cancellationToken: TestContext.Current.CancellationToken);
            var applied = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Optolong L-Ultimate 3nm", apply: true,
                cancellationToken: TestContext.Current.CancellationToken);

            // The dry run must surface it too: finding out before committing is the point.
            dryRun.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.MultiplyLinked);
            applied.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.MultiplyLinked);
            applied.Detail.ShouldBe("2 hard links");
            Sha(File.ReadAllBytes(path)).ShouldBe(before);
            Sha(File.ReadAllBytes(sibling)).ShouldBe(before);
        }

        [Fact]
        public async Task GivenAnExplicitOverride_WhenTaggingAHardLinkedFrame_ThenOnlyTheNamedPathChanges()
        {
            // Pins the hazard itself rather than only the guard: with the override the link IS
            // broken and the sibling keeps the old header. Anyone tempted to make --allow-hardlinked
            // the default should read this test first.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard-link count is only probed on Windows.");
            var dir = CreateTempDir();
            var (path, payload) = WriteFits(dir, "l1.fits", ["IMAGETYP= 'LIGHT'"]);
            var sibling = Path.Combine(dir, "same-frame-other-name.fits");
            Assert.SkipUnless(CreateHardLink(sibling, path, IntPtr.Zero), "Could not create a hard link on this volume.");

            var result = await FitsHeaderEditor.SetStringCardAsync(
                path, "FILTER", "Optolong L-Ultimate 3nm", allowMultiplyLinked: true, apply: true,
                cancellationToken: TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(FitsHeaderEditor.TagOutcome.Tagged);
            HeaderValue(path, "FILTER").ShouldBe("Optolong L-Ultimate 3nm");
            HeaderValue(sibling, "FILTER").ShouldBeNull();
            // Both still hold the same pixels; only the headers have diverged.
            Sha(PayloadOf(path)).ShouldBe(Sha(payload));
            Sha(PayloadOf(sibling)).ShouldBe(Sha(payload));
        }

        [Theory]
        [InlineData("Ha", "FILTER  = 'Ha      '")]
        [InlineData("Optolong L-Ultimate 3nm", "FILTER  = 'Optolong L-Ultimate 3nm'")]
        [InlineData("O'Brien", "FILTER  = 'O''Brien'")]
        public void GivenAValue_WhenFormattingACard_ThenItFollowsTheFitsStringConvention(string value, string expectedPrefix)
        {
            var card = FitsHeaderEditor.FormatStringCard("FILTER", value, "");

            card.Length.ShouldBe(Card);
            card.ShouldStartWith(expectedPrefix);
        }

        [Fact]
        public void GivenAValueTooLongForOneCard_WhenFormatting_ThenItThrowsRatherThanTruncating()
        {
            // Silently truncating a filter name would write a wrong label into irreplaceable data.
            Should.Throw<ArgumentException>(
                () => FitsHeaderEditor.FormatStringCard("FILTER", new string('x', 80), ""));
        }
    }
}
