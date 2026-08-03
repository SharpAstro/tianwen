using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// FILTCLAS is a TianWen-written card; no third-party capture software emits it. So the
    /// FILTCLAS-then-FILTER fallback in the FITS reader is not a nicety, it is the ONLY path by
    /// which a foreign file's filter is ever seen, and a regression there is invisible on our own
    /// output (which always writes both) while silently unfiltering the entire outside world.
    /// </summary>
    [Collection("Imaging")]
    public class FilterHeaderFallbackTests
    {
        private const int Block = 2880;
        private const int Card = 80;

        /// <summary>A minimal valid FITS built card by card, so a test can state exactly which of
        /// FILTER / FILTCLAS is present without our own writer adding the other.</summary>
        private static string WriteFits(string dir, string name, params string[] extraCards)
        {
            var cards = new List<string>
            {
                "SIMPLE  =                    T / C# FITS",
                "BITPIX  =                   16",
                "NAXIS   =                    2 / Dimensionality",
                "NAXIS1  =                   40",
                "NAXIS2  =                   36",
                "BZERO   =                32768",
                "IMAGETYP= 'LIGHT'              / Type of exposure",
            };
            cards.AddRange(extraCards);

            var blocks = (cards.Count + 1 + 35) / 36;
            var header = new byte[blocks * Block];
            header.AsSpan().Fill((byte)' ');
            for (var i = 0; i < cards.Count; i++)
            {
                Encoding.ASCII.GetBytes(cards[i].PadRight(Card), header.AsSpan(i * Card, Card));
            }
            Encoding.ASCII.GetBytes("END".PadRight(Card), header.AsSpan(cards.Count * Card, Card));

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            using var fs = File.Create(path);
            fs.Write(header);
            fs.Write(new byte[40 * 36 * 2]);
            return path;
        }

        private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => Path.Combine(Path.GetTempPath(), "TianWen.FilterFallback", name ?? "x", Guid.NewGuid().ToString("N")[..8]);

        [Theory]
        // The N.I.N.A. / SharpCap / APP shape: FILTER only, no FILTCLAS anywhere in the file.
        [InlineData("FILTER  = 'Ha'                 / Filter name", "HydrogenAlpha", "Ha")]
        [InlineData("FILTER  = 'OIII'               / Filter name", "OxygenIII", "OIII")]
        // Unrecognised manufacturer text still has to survive as its own identity.
        [InlineData("FILTER  = 'Optolong L-Ultimate 3nm'", "Optolong L-Ultimate 3nm", "Optolong L-Ultimate 3nm")]
        [InlineData("FILTER  = 'Antlia ALP-T'", "Antlia ALP-T", "Antlia ALP-T")]
        public void GivenFilterWithoutFiltclas_WhenReadingTheHeader_ThenTheFilterIsResolved(
            string filterCard, string expectedIdentity, string expectedRaw)
        {
            var path = WriteFits(TempDir(), "l1.fits", filterCard);

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Filter.IdentityKey.ShouldBe(expectedIdentity);
            info.Meta.Filter.RawName?.Trim().ShouldBe(expectedRaw);
        }

        [Fact]
        public void GivenNeitherCard_WhenReadingTheHeader_ThenThereIsNoFilter()
        {
            var path = WriteFits(TempDir(), "l1.fits");

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Filter.IdentityKey.ShouldBe("");
        }

        [Fact]
        public void GivenBothCards_WhenReadingTheHeader_ThenFiltclasWinsTheClassification()
        {
            // Our own output shape: FILTCLAS carries the canonical class, FILTER the manufacturer
            // string, and the raw text is preserved for SPCC curve matching.
            var path = WriteFits(TempDir(), "l1.fits",
                "FILTER  = 'Chroma 3nm Ha'",
                "FILTCLAS= 'HydrogenAlpha'");

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Filter.IdentityKey.ShouldBe("HydrogenAlpha");
            info.Meta.Filter.RawName?.Trim().ShouldBe("Chroma 3nm Ha");
        }

        [Fact]
        public void GivenABlankFiltclas_WhenReadingTheHeader_ThenFilterIsStillConsulted()
        {
            // The precise regression: a present-but-empty FILTCLAS must not be taken as a
            // definitive "no filter" and shortcut past FILTER.
            var path = WriteFits(TempDir(), "l1.fits",
                "FILTER  = 'Ha'",
                "FILTCLAS= '        '");

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Filter.IdentityKey.ShouldBe("HydrogenAlpha");
        }
    }
}
