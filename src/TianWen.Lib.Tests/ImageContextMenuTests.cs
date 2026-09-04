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

            items.Length.ShouldBe(4);
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
        public void ThePositionItemIsTheLastREADOUTAndNamesThePixel()
        {
            // Ordering is readouts about the pixel first, in the order they are asked for (sky, sample,
            // position), then the one ACTION. The share link is not a fact about the pixel, so it goes
            // after all three rather than competing with them for the top of the menu.
            var items = ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0));

            var position = items[^2];
            position.Description.ShouldBe("pixel position");
            position.Payload.ShouldBe("12 34");
            position.Label.ShouldBe("Copy position   (12, 34)");

            items[^1].Description.ShouldBe("sky atlas link");
        }

        [Fact]
        public void TheShareLinkPointsAtWhereTheFrameWasLooking()
        {
            var captured = new DateTimeOffset(2026, 1, 18, 23, 26, 51, TimeSpan.Zero);

            var items = ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0), fovDeg: 1.5, capturedUtc: captured);

            var link = items.First(i => i.Description == "sky atlas link");
            link.Payload.ShouldBe(SkyAtlasLink.For(1.0, 2.0, 1.5, captured),
                "the menu is not a second place the URL vocabulary is spelled out");
        }

        [Fact]
        public void AFrameWithNoWcsOffersNoShareLink()
        {
            // There is nothing to point the atlas AT. The link would otherwise be built from a null
            // coordinate and land at RA 0 / Dec 0, which is a real place in the sky and therefore the
            // worst kind of wrong answer.
            ImageContextMenu.ItemsFor(Pixel([0.5f]))
                .Select(i => i.Description).ShouldNotContain("sky atlas link");
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
        public void EveryREADOUTLabelCarriesTheValueSoTheMenuAnswersWithoutCopying()
        {
            // The menu is also a readout: someone who right-clicks to check a coordinate should not have to
            // paste it somewhere to see it. Every label therefore ends in its own value.
            //
            // The share link is the one deliberate exception, and it is exempted BY NAME rather than by
            // relaxing the rule to "most labels": its value is a hundred-character URL, which would be
            // the widest thing in the menu and unreadable at that size, so it is the one item where
            // what gets copied is worth more than what is shown. Any OTHER item that stops carrying its
            // value is still a failure here.
            foreach (var item in ImageContextMenu.ItemsFor(Pixel([0.5f], ra: 3.0, dec: 4.0)))
            {
                if (item.Description == "sky atlas link")
                {
                    continue;
                }
                item.Label.ShouldNotBe($"Copy {item.Description}");
                item.Label.Length.ShouldBeGreaterThan($"Copy {item.Description}".Length);
            }
        }
    }
}
