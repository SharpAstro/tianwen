using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

record QueuedImageWrite(Image Image, ScheduledObservation Observation, DateTimeOffset ExpStartTime, int FrameNumber, TimeSpan ActualSubExposure, int CameraIndex)
{
    /// <summary>
    /// The live cloud judge found this light cloud (#1977): it is written with the <c>MAYBE_CLOUD_</c> prefix. Set by the imaging
    /// loop after the frame's stars are found and before the queue is written, on the one thread that fills and drains it.
    /// </summary>
    public bool SuspectedCloud { get; set; }
}
