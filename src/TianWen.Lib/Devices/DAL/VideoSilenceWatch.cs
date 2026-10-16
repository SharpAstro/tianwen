using System;

namespace TianWen.Lib.Devices.DAL;

/// <summary>
/// How long a video stream may go without a frame, every frame call answering Timeout, before it counts as stalled: the
/// silence watchdog of <c>DALCameraDriver.Video.cs</c>, for a body that goes quiet rather than saying it has stalled (as
/// <c>ASIGetVideoData</c> does with no frame to give). Kept per STREAM, carried from one segment to the next across a
/// recovery's restart, reopen or reset, so a rung's first frame is held to what the stream has shown it needs, never to
/// what a fresh segment would forget. Silences are counted in the frame calls' own waits (<c>VideoFrameWait</c> each).
/// </summary>
/// <remarks>
/// <para><b>The limit</b> (<see cref="Limit"/>) is the longest of:</para>
/// <list type="bullet">
/// <item>twice the exposure plus 1 s, and never under 2 s (<see cref="ForExposure"/>): a ToupTek's own no-packet timeout is
/// the exposure plus 1 s. After an exposure is changed it is the LONGER of the old and the new until
/// <see cref="FramesToTrustAnExposure"/> frames have arrived since, as ToupTek.SDK's OnExposureChanged arms its shorter
/// timeout: a body that finishes the frame in flight (a ZWO may) sends it the OLD exposure after the change, and the new,
/// shorter limit would call that wait a stall (review round 2 of #1605, question 1).</item>
/// <item>twice the longest silence any of the stream's last <see cref="RecentFrames"/> frames came after, plus 1 s, at most
/// <see cref="IntervalCeiling"/>; and before the stream has a frame to go by, the ceiling itself: a stream whose frame period
/// is its READOUT, not its exposure (a 60 MP ZWO at full frame, 16 bits, over USB 2: about 3 s), was stalled on every frame
/// by the exposure's limit and the ladder gave up on it, where the stream before the watchdog waited for ever (question 2).
/// The window and the ceiling keep one slow frame (a USB hiccup) from widening the limit for the rest of the stream.</item>
/// </list>
/// <para><b>What a frame after a change says is about the new mode</b>, so the recent frames are forgotten when the exposure
/// changes (and the frames held under the old one are not counted), and when the window's size, the depth or the readout
/// mode does (the stream stops and starts again in it). A pan keeps them, its readout being the same, and so a recentre does
/// not loosen the watchdog; a binning is a stream's from its start.</para>
/// <para><b>So a body that slows and then dies is called stalled</b> once its silence passes the longest of twice its
/// (held) exposure plus 1 s, 2 s, and twice the longest silence before any of its last 8 frames plus 1 s, that last at most
/// 15 s; one that never sends its first frame, or stops within the first three after a change, once its silence passes
/// 15 s or the (held) exposure's limit, whichever is longer.</para>
/// <para>Touched by one flow at a time, as the stream it belongs to is: a segment's thread, or the recovery between two.</para>
/// </remarks>
internal sealed class VideoSilenceWatch(TimeSpan exposure)
{
    /// <summary>The frames whose silences the limit takes in: one slow frame widens it for this many, then ages out.</summary>
    internal const int RecentFrames = 8;

    /// <summary>Frames to arrive after an exposure change before the limit trusts the new exposure alone.</summary>
    internal const int FramesToTrustAnExposure = 2;

    /// <summary>The most the frames' own silences widen the limit to, and the limit before the stream has a frame to go by.</summary>
    internal static readonly TimeSpan IntervalCeiling = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private readonly TimeSpan[] _recent = new TimeSpan[RecentFrames];
    private int _recentCount;
    private int _nextRecent;
    private TimeSpan _exposure = exposure;
    private TimeSpan _held;
    private int _framesSinceExposureChange = FramesToTrustAnExposure;

    /// <summary>The silence past which the stream counts as stalled, now.</summary>
    public TimeSpan Limit
    {
        get
        {
            var exposure = _framesSinceExposureChange < FramesToTrustAnExposure && _held > _exposure ? _held : _exposure;
            var fromFrames = _recentCount == 0 ? IntervalCeiling : Min(LongestRecent * 2 + Grace, IntervalCeiling);
            var fromExposure = ForExposure(exposure);
            return fromExposure > fromFrames ? fromExposure : fromFrames;
        }
    }

    /// <summary>Twice <paramref name="exposure"/> plus 1 s, and never under 2 s.</summary>
    internal static TimeSpan ForExposure(TimeSpan exposure)
        => exposure * 2 + Grace is var limit && limit > Floor ? limit : Floor;

    /// <summary>A frame arrived after <paramref name="silence"/> with none.</summary>
    public void Frame(TimeSpan silence)
    {
        if (_framesSinceExposureChange < FramesToTrustAnExposure)
        {
            // Taken under the exposure before the change, or the first after it: neither says what the new one needs.
            _framesSinceExposureChange++;
            return;
        }
        _recent[_nextRecent] = silence;
        _nextRecent = (_nextRecent + 1) % RecentFrames;
        _recentCount = Math.Min(_recentCount + 1, RecentFrames);
    }

    /// <summary>The stream's exposure is now <paramref name="exposure"/>: the longer one is held, and the recent frames forgotten.</summary>
    public void ExposureChanged(TimeSpan exposure)
    {
        if (exposure == _exposure)
        {
            return;
        }
        // A change while one is still held keeps the longest of them all.
        _held = _framesSinceExposureChange < FramesToTrustAnExposure && _held > _exposure ? _held : _exposure;
        _exposure = exposure;
        _framesSinceExposureChange = 0;
        ForgetRecentFrames();
    }

    /// <summary>The stream's window size, depth or readout mode changed: the frames before say nothing about its frames after.</summary>
    public void ModeChanged() => ForgetRecentFrames();

    private void ForgetRecentFrames()
    {
        _recentCount = 0;
        _nextRecent = 0;
    }

    private TimeSpan LongestRecent
    {
        get
        {
            var longest = TimeSpan.Zero;
            for (var i = 0; i < _recentCount; i++)
            {
                if (_recent[i] > longest)
                {
                    longest = _recent[i];
                }
            }
            return longest;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
