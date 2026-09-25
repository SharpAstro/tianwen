using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Api
{
    /// <summary>
    /// Renders an OTA's last captured frame for <c>GET /api/v1/preview/{otaIndex}</c> and the ninaAPI
    /// <c>prepared-image</c>, the per-OTA twin of <see cref="GuidePreview"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The frame is the session's, so it is LEASED for the encode.</b> It was read through the bare
    /// reference, which is a buffer the camera may already have taken back, while the guider preview beside it
    /// leased. The slot keeps its frame readable until the next one replaces it, so a refused lease means it
    /// was replaced mid-read, and the next poll finds its successor.</para>
    /// <para><b>The token is the slot's own number</b>
    /// (<see cref="ISessionTelemetry.LastCapturedImageNumber"/>), read before the frame. It used to be the
    /// camera's frame number, which advances as an exposure STARTS: the preview served the previous frame
    /// under the new number, then skipped the new frame when it landed, and so ran a whole sub behind.</para>
    /// <para>Both from P0b item 15 of docs/plans/hardware-in-the-server.md, #752.</para>
    /// </remarks>
    internal static class CapturedImagePreview
    {
        internal static async Task<PreviewRender> RenderAsync(
            ISessionTelemetry telemetry,
            int otaIndex,
            int quality,
            double scale,
            int? ifNoneMatch,
            CancellationToken cancellationToken)
        {
            // Number first, then the frame: the slot advances the number AFTER the frame is in, so this pair can
            // carry a number older than its pixels (one wasted refetch) but never a newer one (a skipped frame).
            var frameNumber = telemetry.LastCapturedImageNumber(otaIndex);

            // The client already has this frame: answer before touching it.
            if (PreviewRender.ClientHas(frameNumber, ifNoneMatch))
            {
                return PreviewRender.Unchanged(frameNumber);
            }

            var images = telemetry.LastCapturedImages;
            if ((uint)otaIndex >= (uint)images.Length)
            {
                return PreviewRender.Missing($"OTA index {otaIndex} out of range (0..{images.Length - 1})", 400);
            }

            if (images[otaIndex] is not { } published)
            {
                // Before the first frame, or after an autofocus run emptied the slot.
                return PreviewRender.Missing($"OTA {otaIndex} has no frame to show yet");
            }

            if (!published.TryLease(out var lease))
            {
                return PreviewRender.Missing($"OTA {otaIndex}'s frame was replaced while it was read; ask again");
            }

            using (lease)
            {
                var jpeg = await PreviewEncoder.EncodeJpegAsync(lease.Image, quality, scale, cancellationToken);
                return PreviewRender.Encoded(jpeg, frameNumber);
            }
        }
    }
}
