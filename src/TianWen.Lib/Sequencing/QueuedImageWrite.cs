using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

record QueuedImageWrite(Image Image, ScheduledObservation Observation, DateTimeOffset ExpStartTime, int FrameNumber);