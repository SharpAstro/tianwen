using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

record QueuedImageWrite(Image Image, Observation Observation, DateTimeOffset ExpStartTime, int FrameNumber);