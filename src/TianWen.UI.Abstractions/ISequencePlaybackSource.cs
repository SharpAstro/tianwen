using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The decode-ahead contract a multi-frame <see cref="IPreviewSource"/> exposes for playback. It exists
/// to keep <b>all</b> per-frame disk I/O off the render thread: the render loop never seeks/decodes a
/// frame inline. Instead a <c>SequencePlayer</c> kicks <see cref="TryStartDecode"/> on a background
/// thread, then polls <see cref="TryPublishDecoded"/> each frame (a lock-free Task hand-off) to swap a
/// freshly decoded frame into the display buffer that <see cref="IPreviewSource.GetChannelData"/> reads.
/// <para>
/// Implementations decode into a buffer distinct from the one the renderer is currently uploading
/// (double-buffering), so a background decode never tears a frame the render thread is reading. Only one
/// decode is in flight at a time (gated by <see cref="IsDecoding"/>), so rapid scrubbing naturally
/// coalesces to the latest target rather than queuing.
/// </para>
/// </summary>
public interface ISequencePlaybackSource
{
    /// <summary>Total number of frames.</summary>
    int FrameCount { get; }

    /// <summary>Index of the frame currently published to the display buffer.</summary>
    int FrameIndex { get; }

    /// <summary>True while a background decode is running (so the caller must not start another).</summary>
    bool IsDecoding { get; }

    /// <summary>
    /// True when the most recent background decode has finished successfully and its frame is waiting to
    /// be published. Lets a frame-paced caller decode ahead during the inter-frame gap and swap the
    /// result in only when its display time arrives (rather than the instant the decode completes).
    /// </summary>
    bool IsDecodeReady { get; }

    /// <summary>
    /// Starts decoding frame <paramref name="index"/> on a background thread into the (hidden) back
    /// buffer. Returns false without starting when a decode is already in flight, the index is already
    /// displayed, the index is out of range, or the source is disposed. Never performs I/O on the
    /// calling (render) thread.
    /// </summary>
    bool TryStartDecode(int index);

    /// <summary>
    /// If a background decode has completed, swaps its result into the display buffer and reports the
    /// newly displayed frame index via <paramref name="frameIndex"/>, returning true (the caller should
    /// then re-upload the texture). Returns false when no decode has completed since the last publish.
    /// Called on the render thread; performs only a buffer-reference swap, no I/O.
    /// </summary>
    bool TryPublishDecoded(out int frameIndex);
}
