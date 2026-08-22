using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The info panel must report what the camera was SET to, and must say nothing where it does not
    /// know.
    /// </summary>
    /// <remarks>
    /// <para>Gain and offset were parsed into <see cref="ImageMeta"/> and written back out to FITS long
    /// before they were ever drawn, so the panel simply omitted two of the three capture settings while
    /// showing the third (exposure). That is the gap these tests pin.</para>
    /// <para>The suppression half matters as much as the presence half: all three fields carry -1 as
    /// their "unknown" sentinel, so a row that formats unconditionally reads <c>Gain: -1</c>, which
    /// looks like a real value read from a real header. A file that knows nothing must produce no row
    /// at all.</para>
    /// </remarks>
    [Collection("UI")]
    public class InfoPanelMetadataTests
    {
        private const int Width = 4;
        private const int Height = 3;

        [Fact]
        public async Task AnAstroCameraFrameReportsGainAndOffset()
        {
            var document = await NewDocumentAsync(Meta() with { Gain = 120, Offset = 30 });

            var lines = InfoPanelData.GetMetadataLines(document);

            lines.ShouldContain("Gain: 120");
            lines.ShouldContain("Offset: 30");
        }

        [Fact]
        public async Task TheCaptureSettingsSitTogether()
        {
            var document = await NewDocumentAsync(
                Meta() with { ExposureDuration = TimeSpan.FromSeconds(30), Gain = 100, Offset = 10 });

            var lines = InfoPanelData.GetMetadataLines(document);

            // Order is the point: exposure, then what the camera was set to, before the optics rows.
            var exposure = lines.IndexOf("Exposure: 30.0s");
            var gain = lines.IndexOf("Gain: 100");
            var offset = lines.IndexOf("Offset: 10");
            exposure.ShouldBeGreaterThanOrEqualTo(0);
            gain.ShouldBe(exposure + 1);
            offset.ShouldBe(gain + 1);
        }

        [Fact]
        public async Task AnUnknownGainOrOffsetProducesNoRowAtAll()
        {
            // The default ImageMeta sentinels, i.e. a file whose header carried neither card.
            var document = await NewDocumentAsync(Meta());

            var lines = InfoPanelData.GetMetadataLines(document);

            lines.ShouldNotContain(l => l.StartsWith("Gain:", StringComparison.Ordinal));
            lines.ShouldNotContain(l => l.StartsWith("Offset:", StringComparison.Ordinal));
            lines.ShouldNotContain(l => l.StartsWith("ISO:", StringComparison.Ordinal));
            // The sentinel must never reach the screen through any row.
            lines.ShouldNotContain(l => l.Contains("-1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ACameraRawReportsIsoInsteadOfGain()
        {
            // The raw import path populates Iso from EXIF and leaves Gain unknown; ISO 51200 is also
            // the case that rules out folding it into the short-typed Gain register.
            var document = await NewDocumentAsync(Meta() with { Iso = 51200 });

            var lines = InfoPanelData.GetMetadataLines(document);

            lines.ShouldContain("ISO: 51200");
            lines.ShouldNotContain(l => l.StartsWith("Gain:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AnUnstatedFrameTypeProducesNoRow()
        {
            // FrameType.None is the enum default, i.e. the file carried no IMAGETYP / FRAMETYP card.
            // Rendering it produced the literal "Frame: None", which reads like a frame kind.
            var document = await NewDocumentAsync(Meta() with { FrameType = FrameType.None });

            var lines = InfoPanelData.GetMetadataLines(document);

            lines.ShouldNotContain(l => l.StartsWith("Frame:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ARealFrameTypeIsStillNamed()
        {
            var document = await NewDocumentAsync(Meta() with { FrameType = FrameType.Flat });

            var lines = InfoPanelData.GetMetadataLines(document);

            lines.ShouldContain("Frame: Flat");
        }

        private static Task<AstroImageDocument> NewDocumentAsync(ImageMeta meta)
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = 1000f + y * Width + x;
                }
            }

            return AstroImageDocument.AdoptImageAsync(
                new Image([plane], BitDepth.Int16, 65535f, 0f, 0f, meta),
                DebayerAlgorithm.None);
        }

        private static ImageMeta Meta()
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
