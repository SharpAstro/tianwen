using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Api
{
    /// <summary>
    /// Renders the live guide-camera frame for <c>GET /api/v1/preview/guider</c>.
    /// <para>
    /// Separate from the endpoint so the part that can actually go wrong is testable without standing up
    /// a host: the frame belongs to the guider driver and is replaced on every exposure, so what needs
    /// pinning is that this borrows it for the encode and gives the borrow back, not that a route exists.
    /// </para>
    /// <para>
    /// <b>Why the guide camera gets its own endpoint rather than joining the per-OTA previews.</b> It is
    /// not an OTA frame: there is one guider for the whole rig, its frames arrive at guiding cadence
    /// rather than per sub, and a remote guider view wants it while the science cameras are mid-exposure
    /// and have nothing new to show. Indexing it as an OTA would also collide with the real OTA numbering,
    /// which comes from the active profile.
    /// </para>
    /// </summary>
    internal static class GuidePreview
    {
        /// <summary>
        /// The reason a render produced no image, or <see langword="null"/> on success. A miss here is
        /// ordinary (the guider may not be looping, or may have just swapped the frame out) and is
        /// reported as a 404 rather than an error.
        /// </summary>
        internal const string NoFrameFailure = "Guider has not produced a frame yet";

        internal static async Task<(byte[]? Jpeg, int FrameNumber, string? Failure)> RenderAsync(
            ISessionTelemetry telemetry,
            int quality,
            double scale,
            CancellationToken cancellationToken)
        {
            // Read the number BEFORE the frame. The token then names a frame no newer than the pixels
            // encoded below, so a client that stores it can never conclude it has already drawn a frame
            // it has not seen. The reverse order can skew the other way, which is the harmful direction.
            var frameNumber = telemetry.LastGuideFrameNumber;

            if (telemetry.LastGuideFrame is not { } published)
            {
                return (null, frameNumber, NoFrameFailure);
            }

            // The guide loop does LastFrame?.Release() before publishing the next one, so holding
            // `published` across the encode would be reading a buffer the camera may already have taken
            // back. Losing that race is normal at guiding cadence and simply means "not right now".
            if (!published.TryLease(out var leased))
            {
                return (null, frameNumber, NoFrameFailure);
            }

            try
            {
                var jpeg = await PreviewEncoder.EncodeJpegAsync(leased, quality, scale, cancellationToken);
                return (jpeg, frameNumber, null);
            }
            finally
            {
                leased.Release();
            }
        }
    }
}
