using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The facts a recording carries about itself, read once when the source opened. A blank string or a null is a field the file
/// does not carry, which the metadata lines suppress.
/// </summary>
/// <param name="Instrument">The camera as the file names it.</param>
/// <param name="Telescope">The telescope as the file names it.</param>
/// <param name="Observer">The observer as the file names it.</param>
/// <param name="Colour">The colour mode in words: Mono, RGB, or a Bayer pattern.</param>
/// <param name="BitDepth">True bits per sample.</param>
/// <param name="FrameCount">Frames in the recording.</param>
/// <param name="Earliest">The earliest time any frame carries, or null where fewer than two frames carry one.</param>
/// <param name="Latest">The latest time any frame carries, or null where fewer than two frames carry one.</param>
/// <param name="FramesPerSecond">The rate over the earliest-to-latest span, or null.</param>
public sealed record PreviewCapture(
    string Instrument,
    string Telescope,
    string Observer,
    string Colour,
    int BitDepth,
    int FrameCount,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    double? FramesPerSecond)
{
    /// <summary>
    /// The span and rate of a recording from the times its frames carry: the earliest and the latest over the frames that carry one
    /// (a capture sorted by quality, as PIPP writes it, is not in time order, #1292), and (stamped frames - 1) over that span.
    /// An unstamped frame (<see cref="DateTimeOffset.MinValue"/>) is skipped. Fewer than two stamped frames, or a span that is not
    /// positive, gives nulls.
    /// </summary>
    public static (DateTimeOffset? Earliest, DateTimeOffset? Latest, double? FramesPerSecond) SpanOf(int count, Func<int, DateTimeOffset?> timeOf)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue, latest = DateTimeOffset.MinValue;
        var stamped = 0;
        for (var i = 0; i < count; i++)
        {
            if (timeOf(i) is { } t && t != DateTimeOffset.MinValue)
            {
                stamped++;
                earliest = t < earliest ? t : earliest;
                latest = t > latest ? t : latest;
            }
        }
        if (stamped < 2 || latest <= earliest)
        {
            return (null, null, null);
        }
        return (earliest, latest, (stamped - 1) / (latest - earliest).TotalSeconds);
    }
}
