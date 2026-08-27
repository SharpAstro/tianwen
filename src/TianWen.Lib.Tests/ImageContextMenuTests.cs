using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{

    /// <summary>
    /// The image right-click menu (<see cref="ImageContextMenu"/>): what it offers for a pixel, and what
    /// each item puts on the clipboard.
    /// </summary>
    /// <remarks>
    /// The menu copies values the viewer has ALREADY resolved for the cursor readout, so these tests are
    /// about the payloads rather than about geometry: a copied coordinate that does not match what the
    /// info pane printed is the failure worth pinning, since the two are read against each other.
    /// </remarks>
    [Collection("UI")]
    public class ImageContextMenuTests
    {
        private static PixelInfo Pixel(float[] values, double? ra = null, double? dec = null)
            => new PixelInfo(12, 34, values, ra, dec);

        [Fact]
        public void APixelWithAWcsOffersItsSkyCoordinatesFirst()
        {
            // RA is carried in HOURS: 5.5 h = 82.5 deg, which is the decimal form the payload must show.
            var items = ImageContextMenu.ItemsFor(Pixel([0.5f], ra: 5.5, dec: -12.25));

            items.Length.ShouldBe(3);
            items[0].Description.ShouldBe("RA / Dec");
            items[0].Label.ShouldStartWith("Copy RA / Dec");

            // Two notations of ONE value: sexagesimal (what the panel shows) then decimal degrees.
            var lines = items[0].Payload.Split('\n');
            lines.Length.ShouldBe(2);
            lines[1].ShouldBe("82.500000 -12.250000");
        }

        [Fact]
        public void APixelWithoutAWcsOffersNoCoordinateItem()
        {
            var items = ImageContextMenu.ItemsFor(Pixel([0.25f, 0.5f, 0.75f]));

            items.Select(i => i.Description).ShouldBe(["pixel value", "pixel position"]);
        }

        [Fact]
        public void TheValueItemCarriesEveryChannelInBothScales()
        {
            var items = ImageContextMenu.ItemsFor(Pixel([0.25f, 0.5f, 0.75f]));

            var value = items.First(i => i.Description == "pixel value");
            var lines = value.Payload.Split('\n');
            lines[0].ShouldBe("0.250000 0.500000 0.750000");
            // The 16-bit form the info pane prints beside each channel, so the two agree.
            lines[1].ShouldBe("16384 32768 49151");
        }

        [Fact]
        public void ThePositionItemIsAlwaysLastAndNamesThePixel()
        {
            var items = ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0));

            items[^1].Description.ShouldBe("pixel position");
            items[^1].Payload.ShouldBe("12 34");
            items[^1].Label.ShouldBe("Copy position   (12, 34)");
        }

        [Fact]
        public void APixelOutsideTheRasterOffersNothing()
        {
            // GetPixelInfo answers this shape for an out-of-bounds query: no samples, no sky. A menu whose
            // only item is "the coordinates you clicked" is noise, so there must be no menu at all.
            ImageContextMenu.ItemsFor(Pixel([])).ShouldBeEmpty();
        }

        [Fact]
        public void AMonoPixelStillOffersItsSingleValue()
        {
            var items = ImageContextMenu.ItemsFor(Pixel([0.125f]));

            var value = items.First(i => i.Description == "pixel value");
            value.Payload.Split('\n')[0].ShouldBe("0.125000");
            value.Label.ShouldBe("Copy value   0.125000");
        }

        [Fact]
        public void EveryLabelCarriesTheValueSoTheMenuAnswersWithoutCopying()
        {
            // The menu is also a readout: someone who right-clicks to check a coordinate should not have to
            // paste it somewhere to see it. Every label therefore ends in its own value.
            foreach (var item in ImageContextMenu.ItemsFor(Pixel([0.5f], ra: 3.0, dec: 4.0)))
            {
                item.Label.ShouldNotBe($"Copy {item.Description}");
                item.Label.Length.ShouldBeGreaterThan($"Copy {item.Description}".Length);
            }
        }
    }
}
