namespace TianWen.Hosting.Api
{
    /// <summary>
    /// What a live-preview request comes to: a picture with its change token, "the frame you have is still
    /// the current one", or the reason there is nothing to show.
    /// </summary>
    /// <remarks>
    /// The endpoints answer <see cref="IsUnchanged"/> as HTTP 304 without leasing or encoding anything, which
    /// is what the token is for: comparing it only after encoding, as the endpoints once did, cost every
    /// poll a full encode per OTA (P0b item 15 of docs/plans/hardware-in-the-server.md, #752).
    /// </remarks>
    internal readonly record struct PreviewRender(byte[]? Jpeg, int FrameNumber, bool IsUnchanged, string? Failure, int StatusCode)
    {
        public static PreviewRender Encoded(byte[] jpeg, int frameNumber) => new PreviewRender(jpeg, frameNumber, false, null, 200);

        public static PreviewRender Unchanged(int frameNumber) => new PreviewRender(null, frameNumber, true, null, 304);

        /// <summary>A miss is ordinary (no frame yet, or one replaced mid-read), so it defaults to 404.</summary>
        public static PreviewRender Missing(string failure, int statusCode = 404) => new PreviewRender(null, 0, false, failure, statusCode);

        /// <summary>
        /// Whether a client that holds <paramref name="ifNoneMatch"/> already has frame <paramref name="frameNumber"/>.
        /// Zero is "nothing published", which no client can hold.
        /// </summary>
        public static bool ClientHas(int frameNumber, int? ifNoneMatch) => frameNumber != 0 && ifNoneMatch == frameNumber;
    }
}
