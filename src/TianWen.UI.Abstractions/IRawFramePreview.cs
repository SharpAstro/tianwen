using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A master source that can also hand over a frame of the capture it stacks, put on the master's grid (<see cref="StackedMasters"/>;
/// not a node's masters nor a master opened as a file, which have no frames here).
/// </summary>
public interface IRawFrameMasters
{
    /// <summary>Frame <paramref name="index"/> placed on <paramref name="stackMaster"/>'s grid, or null when it cannot be (see <see cref="PlanetaryRawFrame"/>).</summary>
    Task<PlanetaryPlacedFrame?> PlacedFrameAsync(int index, Image stackMaster, CancellationToken cancellationToken);
}

/// <summary>
/// A preview source that can also show the frame at the playhead beside its stack, so the viewer's A | B can compare what stacking bought
/// (<see cref="SplitCompare.RawVsStack"/>, #1883). Implemented by <see cref="LiveStackPreviewSource"/> over a capture's own frames.
/// </summary>
/// <remarks>Render thread only, like the source. The frame is built only while <see cref="WantRaw"/> says it is wanted.</remarks>
public interface IRawFramePreview
{
    /// <summary>True when the stack's frames are in hand, so there is a raw frame to show beside it.</summary>
    bool CanShowRaw { get; }

    /// <summary>The playhead's frame on the stack's grid, on the scale the stack is shown on, or null until built (or while not wanted).</summary>
    IPreviewSource? Raw { get; }

    /// <summary>Identity of <see cref="Raw"/>: moves each time a new frame or a new placement is published.</summary>
    int RawGeneration { get; }

    /// <summary>Where <see cref="Raw"/> is drawn from the stack's origin, in the stack's pixels (the registration's shift, negated).</summary>
    (float X, float Y) RawOffset { get; }

    /// <summary>Says whether the raw frame is wanted. Idempotent; call every frame it is compared, and with false when it is not.</summary>
    void WantRaw(bool wanted);
}
