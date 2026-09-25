namespace TianWen.Hosting.Api;

/// <summary>
/// The live-preview endpoints' header contract, named once for the node and its clients.
/// </summary>
/// <remarks>
/// A preview answers with its frame's change token in <see cref="FrameNumber"/> and, quoted, as the
/// <c>ETag</c>. A client names the token it holds in <c>If-None-Match</c>, and the node answers 304 with no
/// body when that is still the current frame, before leasing or encoding anything. A node older than that
/// ignores the header and sends the picture, so a client also compares <see cref="FrameNumber"/> itself.
/// </remarks>
public static class PreviewHeaders
{
    /// <summary>The change token of the frame a preview shows. Compare for difference, not order.</summary>
    public const string FrameNumber = "X-Frame-Number";
}
