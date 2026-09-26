using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Api;

/// <summary>
/// Renders the frame a source shows (<see cref="NodeFrames"/>) as a stretched JPEG: <c>GET /api/v1/preview/{otaIndex}</c>,
/// <c>GET /api/v1/preview/guider</c> and the ninaAPI <c>prepared-image</c>. One renderer for every source, fed the frame and
/// its token as <see cref="NodeFrames"/> resolved them, so an OTA's preview and the guide camera's behave alike.
/// </summary>
/// <remarks>
/// <para><b>The frame is someone else's, so it is LEASED for the encode.</b> A session's slot, a guider's published
/// frame and the node's own preview are each released as the next replaces it, so the encode must hold a lease, never the
/// bare reference, or a JPEG decodes perfectly and shows a buffer the camera has reused. A refused lease means the frame
/// was replaced mid-read, and the next poll finds its successor.</para>
/// <para><b>A client that has the frame is answered before the frame is touched</b>: its <c>If-None-Match</c> names the
/// token, and a match is a 304 with no lease and no encode. The token is <see cref="NodeFrames"/>'s, which names exactly
/// the frame resolved with it; it used to be the camera's exposure counter, which advances as an exposure STARTS, so the
/// preview ran a whole sub behind (P0b item 15 of docs/plans/hardware-in-the-server.md, #752).</para>
/// </remarks>
internal static class FramePreview
{
    internal static async Task<PreviewRender> RenderAsync(
        NodeFrames.Shown shown,
        string what,
        int quality,
        double scale,
        int? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        if (PreviewRender.ClientHas(shown.Number, ifNoneMatch))
        {
            return PreviewRender.Unchanged(shown.Number);
        }

        if (shown.Frame is not { } published)
        {
            return PreviewRender.Missing($"{what} has no frame to show yet");
        }

        if (!published.TryLease(out var lease))
        {
            return PreviewRender.Missing($"{what}'s frame was replaced while it was read; ask again");
        }

        using (lease)
        {
            var jpeg = await PreviewEncoder.EncodeJpegAsync(lease.Image, quality, scale, cancellationToken);
            return PreviewRender.Encoded(jpeg, shown.Number);
        }
    }
}
