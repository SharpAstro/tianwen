using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What a capture's frames say about the capture as a whole.</summary>
public static class PlanetaryFrameStreamExtensions
{
    extension(IPlanetaryFrameStream stream)
    {
        /// <summary>
        /// The instant halfway between the capture's first frame and its last: the epoch its geometry is read at and a
        /// de-rotation carries every frame to (docs/plans/planetary-restoration.md, R1 and R6), or null for an untimed capture.
        /// </summary>
        public DateTimeOffset? MidCapture
            => stream.HasTimestamps && stream.FrameCount > 0 && stream.TimestampOf(0) is { } first && stream.TimestampOf(stream.FrameCount - 1) is { } last
                ? first + ((last - first) / 2)
                : null;
    }
}
