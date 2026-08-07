using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// How many light frames an observation slot is expected to yield, <b>per OTA</b>. The one place this
/// is worked out.
///
/// <para>It is derived from the slot's DURATION, not from the filter plan's frame counts, because the
/// imaging loop is time-bounded rather than count-bounded.
/// <see cref="FilterExposure.Count"/> is a <i>cycling quantum</i> -- "frames at this filter before
/// advancing to the next entry" -- and <c>Session.Imaging</c> only consults it when the plan has more
/// than one entry:
/// <code>if (filterFrameCounters[i] >= currentEntry.Count &amp;&amp; plan.Length > 1)</code>
/// So a single-entry plan never advances and never stops on <c>Count</c>; it exposes until the slot's
/// time runs out. No value of <c>Count</c> can answer "how many frames" there, and summing the counts
/// answers a different question entirely -- <em>one pass of the filter ladder</em>, which is 1 for a
/// single filter and, for a real ladder, one cycle of a loop that then repeats.</para>
///
/// <para>That summing is what this replaces. It made the Home board report
/// <c>frame 5/1</c> for any rig without a filter wheel -- the common OSC case -- while the Session
/// Setup tab, using its own duration-derived formula, said <c>~245</c> for the very same observation.
/// Two formulas for one quantity, disagreeing by 245x on two screens at once.</para>
///
/// <para><b>This is an estimate and callers should present it as one.</b> Real per-frame overhead
/// varies with download size, dither settle and the occasional refocus, and a run can leave a slot
/// early or overrun it.</para>
/// </summary>
public static class FrameCountEstimate
{
    /// <summary>
    /// Wall-clock cost per frame beyond the exposure itself: download, dither and settle. A rough
    /// constant on purpose -- the alternative is a per-rig model whose inputs nobody measures.
    /// </summary>
    public const double PerFrameOverheadSeconds = 10.0;

    /// <summary>
    /// Frames a window yields at a single sub-exposure. Zero for a non-positive window or exposure,
    /// which is what "no plan yet" looks like on a freshly-built schedule.
    /// </summary>
    public static int ForWindow(TimeSpan window, TimeSpan subExposure)
    {
        if (subExposure <= TimeSpan.Zero || window <= TimeSpan.Zero)
        {
            return 0;
        }

        var perFrameSeconds = subExposure.TotalSeconds + PerFrameOverheadSeconds;
        return Math.Max(0, (int)(window.TotalSeconds / perFrameSeconds));
    }

    /// <summary>
    /// Frames a window yields while cycling <paramref name="plan"/>.
    /// <para>
    /// The ladder repeats for the whole slot, so what matters is the mean cost of a frame across one
    /// cycle, weighted by each entry's <see cref="FilterExposure.Count"/> -- an Ha entry shooting three
    /// 600 s subs costs far more of the slot than an L entry shooting one 60 s sub, and a plain average
    /// over the entries would hide that. With a single entry the weights cancel and this reduces exactly
    /// to <see cref="ForWindow"/>, which is why both cases can share one path.
    /// </para>
    /// </summary>
    public static int ForPlan(TimeSpan window, ImmutableArray<FilterExposure> plan)
    {
        if (plan.IsDefaultOrEmpty || window <= TimeSpan.Zero)
        {
            return 0;
        }

        var cycleSeconds = 0.0;
        var cycleFrames = 0;
        foreach (var entry in plan)
        {
            // A non-positive Count would silently drop an entry from the weighting rather than the
            // plan, so it is floored at one frame -- the cycling quantum's own minimum.
            var count = Math.Max(1, entry.Count);
            cycleFrames += count;
            cycleSeconds += count * (entry.SubExposure.TotalSeconds + PerFrameOverheadSeconds);
        }

        if (cycleFrames == 0 || cycleSeconds <= 0.0)
        {
            return 0;
        }

        var meanFrameSeconds = cycleSeconds / cycleFrames;
        return Math.Max(0, (int)(window.TotalSeconds / meanFrameSeconds));
    }

    /// <summary>
    /// The inverse of <see cref="ForWindow"/>: the sub-exposure at which <paramref name="window"/>
    /// yields exactly <paramref name="frames"/>, or <see cref="TimeSpan.Zero"/> when there is no such
    /// exposure (a non-positive window or frame count, or a slot too short to hold that many frames
    /// even at zero exposure).
    ///
    /// <para>This exists for <c>RemoteSessionMirror</c>. The state DTO flattens the filter plan away and
    /// carries only the frame estimate, so the mirror rebuilds a single-entry plan that reproduces it --
    /// and the only way to reproduce a derived number is to invert the derivation. Keeping the inverse
    /// in this file is the point: a copy of the arithmetic in the client is exactly how the two answers
    /// drifted apart the first time.</para>
    ///
    /// <para>Reconstructing from the ESTIMATE rather than from a sub-exposure carried on the wire is
    /// deliberate. A real multi-filter ladder weights its entries, so collapsing it to its first entry's
    /// sub-exposure would give the mirror a different answer than the node computed (48 frames against
    /// 60, for one Ha-plus-luminance plan). Inverting reproduces the node's number whatever the plan
    /// behind it was.</para>
    /// </summary>
    public static TimeSpan SubExposureForFrames(TimeSpan window, int frames)
    {
        if (frames <= 0 || window <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var perFrameSeconds = window.TotalSeconds / frames;
        var subExposureSeconds = perFrameSeconds - PerFrameOverheadSeconds;
        return subExposureSeconds > 0.0 ? TimeSpan.FromSeconds(subExposureSeconds) : TimeSpan.Zero;
    }
}
