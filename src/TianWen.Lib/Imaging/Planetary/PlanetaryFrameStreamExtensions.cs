using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What a capture's frames say about the capture as a whole.</summary>
public static class PlanetaryFrameStreamExtensions
{
    extension(IPlanetaryFrameStream stream)
    {
        /// <summary>
        /// The earliest and the latest of the capture's frame times, read over EVERY frame, or null for an untimed capture. Never
        /// its first and last FRAMES' times (#1292): PIPP can write a capture sorted by quality, and the owner's 2021-08-01 Saturn
        /// (2,329 frames) starts at 11:38:40.4 and ends at 11:35:38.3 of a run from 11:32:22.7 to 11:40:22.0, with 1,167 of its
        /// steps going back in time. Every span, epoch and quarter of a capture is read from here.
        /// </summary>
        public (DateTimeOffset Earliest, DateTimeOffset Latest)? CaptureSpan
        {
            get
            {
                if (!stream.HasTimestamps)
                {
                    return null;
                }
                DateTimeOffset? earliest = null, latest = null;
                for (var i = 0; i < stream.FrameCount; i++)
                {
                    if (stream.TimestampOf(i) is not { } time)
                    {
                        continue;
                    }
                    if (earliest is not { } soonest || time < soonest)
                    {
                        earliest = time;
                    }
                    if (latest is not { } last || time > last)
                    {
                        latest = time;
                    }
                }
                return earliest is { } from && latest is { } to ? (from, to) : null;
            }
        }

        /// <summary>
        /// The instant halfway between the capture's earliest frame and its latest (<see cref="CaptureSpan"/>): the epoch its geometry is
        /// read at and a de-rotation carries every frame to (docs/plans/planetary-restoration.md, R1 and R6), or null for an untimed
        /// capture.
        /// </summary>
        public DateTimeOffset? MidCapture
            => stream.CaptureSpan is { } span ? span.Earliest + ((span.Latest - span.Earliest) / 2) : null;
    }
}
