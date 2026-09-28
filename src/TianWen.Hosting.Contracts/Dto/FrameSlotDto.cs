using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A stream's frame waiting in shared memory instead of on the socket (P4b of docs/plans/hardware-in-the-server.md, #932):
/// the node's answer to an ask, as a text message, when the client asked for the shared-memory carrier over this machine's
/// socket (<see cref="Api.FrameStreamWire.SharedMemory"/>). The client copies the frame out of the slot and checks the
/// slot's generation around the copy (<see cref="FrameSlotReader"/>); a slot written again meanwhile means asking again.
/// </summary>
public sealed record FrameSlotDto
{
    /// <summary>The node's number for the frame, as a frame sent on the socket carries it.</summary>
    public required int Number { get; init; }

    /// <summary>The section's name: a Windows section's, or a file's path elsewhere.</summary>
    public required string Map { get; init; }

    /// <summary>Each slot's size in bytes.</summary>
    public required long Capacity { get; init; }

    /// <summary>Which slot holds the frame.</summary>
    public required int Slot { get; init; }

    /// <summary>The slot's generation once the frame was in it.</summary>
    public required long Generation { get; init; }

    /// <summary>The frame's length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>The slot this names, for <see cref="FrameSlotReader.TryRead"/>.</summary>
    public FrameSlot ToSlot() => new FrameSlot(Map, Capacity, Slot, Generation, Length);

    /// <summary>The answer for frame <paramref name="number"/>, written to <paramref name="slot"/>.</summary>
    public static FrameSlotDto Of(int number, FrameSlot slot) => new FrameSlotDto
    {
        Number = number,
        Map = slot.Map,
        Capacity = slot.Capacity,
        Slot = slot.Slot,
        Generation = slot.Generation,
        Length = slot.Length,
    };
}
