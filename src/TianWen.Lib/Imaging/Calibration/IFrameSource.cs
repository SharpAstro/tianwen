using System.Collections.Generic;
using System.Threading;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Streaming source of FITS frames for the stacking pipeline. Implementations
/// enumerate frames lazily so only the small <see cref="FrameInfo"/> record
/// (path + parsed header) is in memory at any time; pixel data is loaded on
/// demand via <see cref="FrameInfo.LoadFullAsync"/>.
/// <para>
/// The contract is metadata-only enumeration: implementations must not retain
/// pixel buffers across yielded items. Consumers that need pixel data call
/// <see cref="FrameInfo.LoadFullAsync"/> per frame and release the returned
/// <see cref="Image"/> as soon as they're done with it (e.g. after appending
/// to a running median accumulator).
/// </para>
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Enumerates the frames in this source, parsing FITS headers as it goes.
    /// Frames that fail to read or parse are silently skipped — corrupt files
    /// in a capture folder shouldn't stop the rest of the pipeline.
    /// </summary>
    IAsyncEnumerable<FrameInfo> EnumerateAsync(CancellationToken cancellationToken = default);
}
