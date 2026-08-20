using Shouldly;
using System.IO;
using System.Text;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.TryReadFitsFile(string, out Image?)"/> must ANSWER for a file it cannot read,
/// never throw. It used to throw, alone among the readers beside it -- <c>TryReadFitsHeader</c> has
/// caught since it was written and <c>TryReadTiff</c> wraps its whole body -- and the consequence was
/// not a visible crash but a silent nothing: the viewer loads inside a <c>Task.Run</c>, so an escaping
/// exception became an unobserved fault and clicking the file did nothing, with no log and no message.
///
/// <para><b>Which of these actually pin the guard, measured rather than assumed.</b> The two
/// file-access cases fail without it; the four content cases pass either way, because FITS.Lib already
/// answers gracefully for them. They are kept deliberately and labelled honestly: they cover the
/// tolerance FITS.Lib currently has, so a regression THERE is caught here, but nobody should read them
/// as testing this guard. Writing them without checking is how a test file ends up looking like
/// coverage it does not have.</para>
///
/// <para>The trigger found in the wild was NASA's own reference sample
/// <c>FOCx38i0101t_c0f.fits</c>, whose <c>DATE-OBS = ' 2/07/96'</c> -- a space-padded single-digit day,
/// legal in the old FITS convention -- made FITS.Lib's <c>BasicHDU.ObservationDate</c> catch the parse
/// failure, assign null, and then cast that null to <see cref="System.DateTime"/>. Fixed in FITS.Lib
/// 5.0.401. That the fix is upstream is exactly why the contract is pinned here.</para>
/// </summary>
[Collection("Imaging")]
public class FitsReadContractTests
{
    /// <summary>
    /// VERIFIED to fail without the guard: the path not existing throws from the reader's constructor,
    /// before any parsing. A guard placed deeper, around the parse alone, would miss it.
    /// </summary>
    [Fact]
    public void AMissingFileIsAnswered_NotThrown()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");

        Should.NotThrow(() => Image.TryReadFitsFile(path, out _, out _)).ShouldBeFalse();
    }

    /// <summary>
    /// VERIFIED to fail without the guard, and the realistic case of the two: a capture program still
    /// writing the frame holds it exclusively, and the folder sidebar will happily offer it for opening
    /// while that is true.
    /// </summary>
    [Fact]
    public void AFileHeldExclusivelyByAnotherProcessIsAnswered_NotThrown()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("SIMPLE  =                    T"));
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            Should.NotThrow(() => Image.TryReadFitsFile(path, out _, out _)).ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// These pass with the guard REMOVED as well -- FITS.Lib already returns rather than throwing for
    /// malformed content. Kept as coverage of that tolerance, not of the guard.
    /// </summary>
    [Theory]
    [InlineData("SIMPLE  =                    T / truncated mid-card")]
    [InlineData("SIMPLE  =                    T    not a header at all")]
    // What a mis-renamed download looks like: an error page saved as .fits.
    [InlineData("<html><body>404 Not Found</body></html>")]
    // Zero bytes is a real outcome of an interrupted download.
    [InlineData("")]
    public void UnreadableContentIsAnswered_NotThrown(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        try
        {
            var read = Should.NotThrow(() => Image.TryReadFitsFile(path, out var image, out _) ? image : null);
            read.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
