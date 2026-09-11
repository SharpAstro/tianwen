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

            items[^1].Description.ShouldBe("sky atlas");
            items[^1].Action.ShouldBe(ImageContextMenuAction.OpenUrl, "the one entry that is not a copy");
        }

        [Fact]
        public void TheShareLinkPointsAtWhereTheFrameWasLooking()
        {
            var captured = new DateTimeOffset(2026, 1, 18, 23, 26, 51, TimeSpan.Zero);

            var items = ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0), fovDeg: 1.5, capturedUtc: captured);

            var link = items.First(i => i.Description == "sky atlas");
            link.Payload.ShouldBe(SkyAtlasLink.For(1.0, 2.0, 1.5, captured),
                "the menu is not a second place the URL vocabulary is spelled out");
        }

        /// <summary>
        /// A click that resolved an object hands the atlas that object, not just its coordinates.
        /// </summary>
        /// <remarks>
        /// The gap this closes was visible in one screenshot: the menu's own first row read "Copy
        /// object name   NGC 7204A" while the link below it carried ra/dec/fov/t and nothing else, so
        /// the atlas opened on the right sky with the object unselected and unnamed among its
        /// neighbours. The DESIGNATION is the token because it is what the atlas's search resolves
        /// unambiguously; a common name can be shared between objects or missing entirely.
        /// </remarks>
        [Fact]
        public void TheShareLinkCarriesTheObjectTheClickResolved()
        {
            var items = ImageContextMenu.ItemsFor(
                Pixel([0.1f], ra: 1.0, dec: 2.0),
                nearest: new ImageContextMenuObject("Lagoon Nebula", "NGC 6523"));

            items.First(i => i.Description == "sky atlas").Payload
                .ShouldEndWith("&object=NGC%206523");
        }

        /// <summary>
        /// A right-click on empty sky is a pointing, so the link stays one.
        /// </summary>
        [Fact]
        public void AClickOnNoObjectSharesAPointingOnly()
            => ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0))
                .First(i => i.Description == "sky atlas").Payload
                .ShouldNotContain("object=");

        [Fact]
        public void AFrameWithNoWcsOffersNoAtlasAction()
        {
            // There is nothing to point the atlas AT. The link would otherwise be built from a null
            // coordinate and land at RA 0 / Dec 0, which is a real place in the sky and therefore the
            // worst kind of wrong answer.
            ImageContextMenu.ItemsFor(Pixel([0.5f]))
                .Select(i => i.Description).ShouldNotContain("sky atlas");
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
            // The atlas entry is the one deliberate exception, and it is exempted BY NAME rather than by
            // relaxing the rule to "most labels": its value is a hundred-character URL, which would be
            // the widest thing in the menu and unreadable at that size -- and it is the one entry that
            // does something rather than answering something, so there is no value to show. Any OTHER
            // item that stops carrying its value is still a failure here.
            foreach (var item in ImageContextMenu.ItemsFor(Pixel([0.5f], ra: 3.0, dec: 4.0)))
            {
                if (item.Description == "sky atlas")
                {
                    continue;
                }
                item.Label.ShouldNotBe($"Copy {item.Description}");
                item.Label.Length.ShouldBeGreaterThan($"Copy {item.Description}".Length);
            }
        }

        /// <summary>
        /// A click on a marked object is nearly always about the object, so its two entries lead. Both
        /// halves are offered because they answer different questions: the name is what a person
        /// searches for, the designation is what another tool takes.
        /// </summary>
        [Fact]
        public void AClickOnACataloguedObjectOffersItsNameAndItsDesignation()
        {
            var items = ImageContextMenu.ItemsFor(
                Pixel([0.1f], ra: 18.06, dec: -24.38),
                nearest: new ImageContextMenuObject("Lagoon Nebula", "NGC 6523"));

            items[0].Description.ShouldBe("object name");
            items[0].Payload.ShouldBe("Lagoon Nebula");
            items[0].Label.ShouldBe("Copy object name   Lagoon Nebula");
            items[1].Description.ShouldBe("catalogue number");
            items[1].Payload.ShouldBe("NGC 6523");
            items[2].Description.ShouldBe("RA / Dec");
        }

        /// <summary>An object with no common name has its designation AS its name, and two entries
        /// carrying the same string is noise rather than a choice.</summary>
        [Fact]
        public void AnObjectWithNoCommonNameOffersOneEntryRatherThanTwoIdenticalOnes()
        {
            var items = ImageContextMenu.ItemsFor(
                Pixel([0.1f], ra: 1.0, dec: 2.0),
                nearest: new ImageContextMenuObject("IC 4606", "IC 4606"));

            items.Count(i => i.Description is "object name" or "catalogue number").ShouldBe(1);
            items[0].Payload.ShouldBe("IC 4606");
        }

        [Fact]
        public void AClickOnNothingCataloguedOffersNoObjectEntries()
            => ImageContextMenu.ItemsFor(Pixel([0.1f], ra: 1.0, dec: 2.0))
                .Select(i => i.Description)
                .ShouldNotContain("object name");

        // --- the SELECTION's atlas entry ---

        private static SkyMapInfoPanelData Selected(string name = "Lagoon Nebula",
            string designation = "NGC 6523", double raHours = 18.06, double dec = -24.38)
            => SkyMapInfoPanelData.FromPosition(
                name, raHours, dec, double.NaN, double.NaN, DateTimeOffset.UnixEpoch, default)
                with
            { Canonical = designation };

        /// <summary>
        /// A selection earns its OWN atlas entry, centred on the object rather than on the click.
        /// </summary>
        /// <remarks>
        /// <b>A second entry rather than re-pointing the first.</b> The existing entry opens the atlas
        /// at the pixel that was right-clicked, and quietly aiming it somewhere else on the frame
        /// whenever a selection happens to exist would make a positional action mean different things
        /// depending on state. So both are offered, each labelled with what it acts on -- and the
        /// coordinates prove which is which, since the click here is nowhere near the selection.
        /// </remarks>
        [Fact]
        public void ASelectionAddsItsOwnAtlasEntryPointedAtTheObject()
        {
            var items = ImageContextMenu.ItemsFor(
                Pixel([0.1f], ra: 1.0, dec: 2.0), fovDeg: 1.5, selection: Selected());

            var atlas = items.Where(i => i.Description == "sky atlas").ToArray();
            atlas.Length.ShouldBe(2, "the positional entry and the selection's own");

            // The positional one carries the CLICK: 1.0 h = 15 deg.
            atlas[0].Payload.ShouldContain("ra=15");
            atlas[0].Label.ShouldBe("Open in sky atlas (web)");

            // The selection's carries the OBJECT, and says whose it is. The space is %20 rather than
            // "+": SkyAtlasLink escapes with Uri.EscapeDataString, which is the whole reason the
            // escaping lives there and not at each end of the link.
            atlas[1].Label.ShouldBe("Open Lagoon Nebula in sky atlas (web)");
            atlas[1].Payload.ShouldContain("object=NGC%206523");

            // 18.06 h = 270.9 deg, so the two entries really are pointed at different places.
            atlas[1].Payload.ShouldContain("ra=270.9");
        }

        /// <summary>
        /// When the right-click resolved the SAME object that is selected, the second entry is not
        /// offered: two rows doing the same thing is the noise the designation/name pair above avoids
        /// for the same reason.
        /// </summary>
        [Fact]
        public void ASelectionAlreadyUnderTheClickAddsNoSecondEntry()
        {
            var items = ImageContextMenu.ItemsFor(
                Pixel([0.1f], ra: 18.06, dec: -24.38), fovDeg: 1.5,
                nearest: new ImageContextMenuObject("Lagoon Nebula", "NGC 6523"),
                selection: Selected());

            items.Count(i => i.Description == "sky atlas").ShouldBe(1);
        }

        /// <summary>
        /// A pixel with no sky position offers no atlas entry at all, selection or not -- the link
        /// needs coordinates to centre on, and the selection's entry rides inside that same gate.
        /// </summary>
        [Fact]
        public void WithoutAWcsASelectionStillOffersNoAtlasEntry()
            => ImageContextMenu.ItemsFor(Pixel([0.1f]), fovDeg: 1.5, selection: Selected())
                .Count(i => i.Description == "sky atlas")
                .ShouldBe(0);
    }
}
