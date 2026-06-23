using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Drives frame advancement for a multi-frame <see cref="ISequencePlaybackSource"/> entirely off the
/// render thread's I/O path, and -- crucially -- <b>frame-paced</b>: it decodes the next frame ahead
/// during the inter-frame gap and publishes it only when its display time arrives, so the render loop
/// renders exactly once per displayed frame and idles in between (no busy-spin, low GPU). This mirrors
/// the standalone viewer's "nothing new to draw -> idle" behaviour while keeping all decode off-thread.
/// <para>
/// Pure and clock-injected (the caller passes monotonic <c>nowSeconds</c>), so it is headless-testable:
/// feed a fake source + a synthetic clock + an explicit decode-completion step and assert pacing,
/// single-decode gating, seeks, and loop wraparound, with no GPU and no real disk.
/// </para>
/// </summary>
public sealed class SequencePlayer
{
    private double _nextDueSeconds;   // wall-clock time the next frame should be displayed
    private int _lookahead = -1;      // frame index currently decoding ahead (or -1 = none requested)
    private int? _seek;               // explicit seek target (scrub / step); bypasses pacing
    private bool _wasPlaying;
    private float _lastFps = -1f;

    /// <summary>True while an explicit seek (scrub/step) is still resolving -- the render loop keeps
    /// polling so the seeked frame shows promptly.</summary>
    public bool SeekPending => _seek.HasValue;

    /// <summary>Resets all playback timing. Call when the source changes (new SER opened / cleared).</summary>
    public void Reset()
    {
        _nextDueSeconds = 0;
        _lookahead = -1;
        _seek = null;
        _wasPlaying = false;
        _lastFps = -1f;
    }

    private static double FrameInterval(ViewerState state) => 1.0 / Math.Max(state.PlaybackFps, 1f);

    /// <summary>
    /// Advances playback by one tick. <paramref name="nowSeconds"/> is a monotonic elapsed-seconds value
    /// (e.g. a <see cref="System.Diagnostics.Stopwatch"/>). Returns true if a newly decoded frame was
    /// published this tick (the caller should set <see cref="ViewerState.NeedsTextureUpdate"/> and
    /// re-render). Returns false between frames so the caller can let the render loop idle.
    /// </summary>
    public bool Tick(ISequencePlaybackSource source, ViewerState state, double nowSeconds)
    {
        if (source.FrameCount <= 1)
        {
            return false;
        }

        // Take any new explicit seek request (scrub / step / Home / End).
        if (state.RequestedFrame is { } requested)
        {
            _seek = Math.Clamp(requested, 0, source.FrameCount - 1);
            state.RequestedFrame = null;
        }

        // SEEK path: decode the target and publish it as soon as it is ready, ignoring playback pacing.
        if (_seek is { } target)
        {
            var publishedSeek = false;
            if (source.FrameIndex == target)
            {
                _seek = null; // already there
            }
            else if (source.IsDecodeReady && _lookahead == target)
            {
                source.TryPublishDecoded(out var shown);
                state.FrameIndex = shown;
                publishedSeek = true;
                _lookahead = -1;
                if (shown == target)
                {
                    _seek = null;
                }
            }
            else if (!source.IsDecoding && _lookahead != target && source.TryStartDecode(target))
            {
                _lookahead = target;
            }

            // Resume playback pacing from the seeked position, whenever playback is next running.
            _nextDueSeconds = nowSeconds + FrameInterval(state);
            return publishedSeek;
        }

        if (!state.IsPlaying)
        {
            return false; // paused, no seek -> idle
        }

        // Re-anchor the schedule on play-start or fps change so advance stays continuous.
        if (!_wasPlaying || state.PlaybackFps != _lastFps)
        {
            _nextDueSeconds = nowSeconds + FrameInterval(state);
            _lookahead = -1;
        }
        _wasPlaying = state.IsPlaying;
        _lastFps = state.PlaybackFps;

        var nextFrame = (source.FrameIndex + 1) % source.FrameCount;
        var published = false;

        // Publish the look-ahead frame once its display time has arrived and it is decoded.
        if (nowSeconds >= _nextDueSeconds && source.IsDecodeReady && _lookahead == nextFrame)
        {
            source.TryPublishDecoded(out var shown);
            state.FrameIndex = shown;
            published = true;
            _lookahead = -1;
            nextFrame = (source.FrameIndex + 1) % source.FrameCount;

            // Schedule the following frame; if we have fallen behind (slow decode / long idle), drop the
            // backlog rather than catch-up-spiral.
            _nextDueSeconds += FrameInterval(state);
            if (_nextDueSeconds < nowSeconds)
            {
                _nextDueSeconds = nowSeconds + FrameInterval(state);
            }
        }

        // Keep the next frame decoding ahead during the gap so it is ready by the time it is due.
        if (_lookahead != nextFrame && !source.IsDecoding && !source.IsDecodeReady && source.TryStartDecode(nextFrame))
        {
            _lookahead = nextFrame;
        }

        return published;
    }
}
