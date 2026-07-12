using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the archive-scan fault tolerance surfaced by the real 62k-file dataset run: a malformed /
    /// truncated FITS — or one whose header FITS.Lib itself can't parse (e.g. <c>BasicHDU.ObservationDate</c>
    /// NREs by unboxing a null when <c>DATE-OBS</c> is missing/unparseable) — must be SKIPPED, not fatal.
    /// One bad file cannot abort a whole-archive scan.
    /// </summary>
    [Collection("Imaging")]
    public class FitsScanResilienceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fitsresil-" + Guid.NewGuid().ToString("N")[..8]);

        public FitsScanResilienceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void TryReadFitsHeader_OnMalformedFile_ReturnsFalse_DoesNotThrow()
        {
            var path = Path.Combine(_dir, "junk.fits");
            File.WriteAllText(path, "SIMPLE  = not a real FITS header, just text with a .fits extension\n");

            // Must return false rather than propagate (the TryX contract) -- Should.NotThrow guards
            // the regression the real archive scan hit.
            var info = default(FrameInfo);
            Should.NotThrow(() => Image.TryReadFitsHeader(path, out info));
            info.ShouldBeNull();
        }

        [Fact]
        public async Task FitsFolderFrameSource_SkipsUnreadable_StillYieldsValidFrames()
        {
            var ct = TestContext.Current.CancellationToken;
            // Two valid FITS + one corrupt file sharing the .fits extension in the same folder.
            RgbBayerSyntheticFixture.WriteSyntheticDarks(_dir); // dark_00.fits, dark_01.fits
            File.WriteAllText(Path.Combine(_dir, "corrupt.fits"), "garbage");

            var frames = new List<FrameInfo>();
            await foreach (var frame in new FitsFolderFrameSource(_dir, recursive: false).EnumerateAsync(ct))
            {
                frames.Add(frame);
            }

            // The corrupt file is skipped; the scan completes and yields the two valid darks.
            frames.Count.ShouldBe(RgbBayerSyntheticFixture.DarkCount);
        }
    }
}
