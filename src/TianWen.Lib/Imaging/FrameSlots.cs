using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Where a frame waits in shared memory for a client on this machine to copy it out (P4b of
/// docs/plans/hardware-in-the-server.md, #932): the section (<see cref="Map"/>, <see cref="Capacity"/>), the slot, and the
/// slot's generation when the frame was written, which the reader checks before and after it copies.
/// </summary>
/// <param name="Map">The section's name (<see cref="SharedMemorySection.Name"/>).</param>
/// <param name="Capacity">Each slot's size in bytes, which with the layout gives the section's.</param>
/// <param name="Slot">Which of the two slots holds the frame.</param>
/// <param name="Generation">The slot's generation once the frame was in it: even, and moved on by every later write.</param>
/// <param name="Length">The frame's length in bytes (<see cref="FrameWire"/>'s shape).</param>
public readonly record struct FrameSlot(string Map, long Capacity, int Slot, long Generation, long Length);

/// <summary>
/// The layout both ends of a slot section agree on. A 4096-byte head, then the two slots' data, each
/// <see cref="FrameSlot.Capacity"/> bytes. The head holds the magic and version, each slot's capacity, and per slot a
/// control line: its generation (a seqlock, odd while it is written) and the length of the frame in it.
/// </summary>
internal static class FrameSlotLayout
{
    public const int Slots = 2;
    public const int HeadBytes = 4096;
    public const uint Magic = 0x4C535754; // "TWSL", little endian
    public const int Version = 1;

    private const int ControlBase = 64;
    private const int ControlStride = 64;

    public static long SectionBytes(long capacity) => HeadBytes + Slots * capacity;

    public static long GenerationOffset(int slot) => ControlBase + slot * ControlStride;

    public static long LengthOffset(int slot) => ControlBase + slot * ControlStride + sizeof(long);

    public static long DataOffset(int slot, long capacity) => HeadBytes + slot * capacity;
}

/// <summary>
/// The node's side of a stream's shared-memory carrier (P4b): two slots, each behind a seqlock. A write makes the slot's
/// generation odd, writes the frame, and makes it even again; nothing a client does can hold it up, so a dead or stalled
/// client never blocks the node, which is the reason there is no acknowledgement. One writer per stream: the stream's
/// own loop, which writes only when its client asks.
/// </summary>
/// <remarks>
/// The section is made on the first frame and made again, larger, for a frame that does not fit (a larger window, a
/// colour master), so nothing needs to know the largest frame a source can produce. A frame is written exactly as
/// <see cref="FrameWire"/> sends it over the socket, so a client reads it with the same code, pixel for pixel.
/// </remarks>
public sealed class FrameSlotWriter(string prefix, string unixDirectory) : IDisposable
{
    // Room for a frame a little larger than the one that asked for it, rounded to 64 KiB, so a window a few pixels wider
    // does not make the section again.
    private const long Headroom = 64 * 1024;

    private SharedMemorySection? _section;
    private long _capacity;
    private int _next;
    private readonly int[] _numbers = new int[FrameSlotLayout.Slots];
    private readonly long[] _generations = new long[FrameSlotLayout.Slots];

    /// <summary>How many times the section has been made, the first included: a slot that keeps up needs one.</summary>
    public int SectionsMade { get; private set; }

    /// <summary>
    /// Writes <paramref name="frame"/>, the node's frame number <paramref name="number"/>, into a slot and answers where it
    /// is. A frame a slot already holds is answered from there, unwritten. The caller keeps the frame, leased for the call.
    /// </summary>
    public FrameSlot Write(int number, Image frame)
    {
        if (_section is { } held)
        {
            for (var slot = 0; slot < FrameSlotLayout.Slots; slot++)
            {
                if (_numbers[slot] == number && _generations[slot] != 0)
                {
                    return new FrameSlot(held.Name, _capacity, slot, _generations[slot], held.Int64At(FrameSlotLayout.LengthOffset(slot)));
                }
            }
        }

        var prepared = FrameWire.Prepare(frame);
        if (prepared.Length > int.MaxValue)
        {
            throw new InvalidOperationException($"A frame of {prepared.Length} bytes is larger than a slot can hold");
        }
        var section = _section is { } current && prepared.Length <= _capacity ? current : MakeSection(prepared.Length);

        var slotToWrite = _next;
        _next = (_next + 1) % FrameSlotLayout.Slots;
        ref var generation = ref section.Int64At(FrameSlotLayout.GenerationOffset(slotToWrite));

        // Odd: being written. Interlocked is a full fence each side, so no byte of the frame moves outside the pair.
        Interlocked.Increment(ref generation);
        section.Int64At(FrameSlotLayout.LengthOffset(slotToWrite)) = prepared.Length;
        FrameWire.Write(frame, prepared, section.Bytes(FrameSlotLayout.DataOffset(slotToWrite, _capacity), (int)prepared.Length));
        var written = Interlocked.Increment(ref generation);

        _numbers[slotToWrite] = number;
        _generations[slotToWrite] = written;
        return new FrameSlot(section.Name, _capacity, slotToWrite, written, prepared.Length);
    }

    public void Dispose()
    {
        _section?.Dispose();
        _section = null;
    }

    private SharedMemorySection MakeSection(long frameLength)
    {
        var capacity = (frameLength + frameLength / 4 + Headroom - 1) / Headroom * Headroom;
        var section = SharedMemorySection.Create(FrameSlotLayout.SectionBytes(capacity), prefix, unixDirectory);
        var head = section.Bytes(0, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(head, FrameSlotLayout.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], FrameSlotLayout.Version);
        BinaryPrimitives.WriteInt64LittleEndian(head[8..], capacity);

        // A client still copying out of the old section keeps it until it lets go; the name it has is no longer answered.
        _section?.Dispose();
        _section = section;
        _capacity = capacity;
        _next = 0;
        Array.Clear(_numbers);
        Array.Clear(_generations);
        SectionsMade++;
        return section;
    }
}

/// <summary>
/// A client's side of a stream's shared-memory carrier (P4b): copies the frame a <see cref="FrameSlot"/> names out of the
/// node's section into planes its <see cref="FrameReader"/> recycles, so what it hands on is an <see cref="Image"/> like
/// any other. A slot written again while it was copied is detected by its generation and the copy dropped; the caller asks
/// for the next frame.
/// </summary>
public sealed class FrameSlotReader : IDisposable
{
    private SharedMemorySection? _section;

    /// <summary>Frames dropped because the node wrote their slot again before the copy was done.</summary>
    public int Torn { get; private set; }

    /// <summary>
    /// The frame <paramref name="slot"/> names, read through <paramref name="reader"/> (the image is the caller's to
    /// release); null when its slot was written again before or while it was copied.
    /// </summary>
    public Image? TryRead(FrameSlot slot, FrameReader reader)
    {
        if (slot.Slot is < 0 or >= FrameSlotLayout.Slots || slot.Length <= 0 || slot.Length > slot.Capacity || slot.Length > int.MaxValue)
        {
            throw new InvalidDataException($"A frame slot that cannot be: slot {slot.Slot}, {slot.Length} bytes in {slot.Capacity}");
        }
        var section = Open(slot);
        ref var generation = ref section.Int64At(FrameSlotLayout.GenerationOffset(slot.Slot));

        if (Volatile.Read(ref generation) != slot.Generation)
        {
            Torn++;
            return null;
        }

        Image frame;
        try
        {
            frame = reader.Read(section.Bytes(FrameSlotLayout.DataOffset(slot.Slot, slot.Capacity), (int)slot.Length));
        }
        catch (Exception) when (Volatile.Read(ref generation) != slot.Generation)
        {
            // Overwritten mid-copy: what was read was half of two frames, which fails however its bytes fall (a header that
            // is not JSON, sizes that do not fit). A failure with the slot unmoved is a real one, and goes on up.
            Torn++;
            return null;
        }

        // Every byte of the copy is read before the generation is looked at again.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref generation) != slot.Generation)
        {
            frame.Release();
            Torn++;
            return null;
        }
        return frame;
    }

    public void Dispose()
    {
        _section?.Dispose();
        _section = null;
    }

    private SharedMemorySection Open(FrameSlot slot)
    {
        if (_section is { } held && held.Name == slot.Map)
        {
            return held;
        }

        var section = SharedMemorySection.OpenForReading(slot.Map, FrameSlotLayout.SectionBytes(slot.Capacity));
        try
        {
            var head = section.Bytes(0, 16);
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != FrameSlotLayout.Magic
                || BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != FrameSlotLayout.Version
                || BinaryPrimitives.ReadInt64LittleEndian(head[8..]) != slot.Capacity)
            {
                throw new InvalidDataException($"{slot.Map} is not a frame slot section of this version and capacity");
            }
        }
        catch
        {
            section.Dispose();
            throw;
        }

        // The section the node made before this one is no longer answered.
        _section?.Dispose();
        _section = section;
        return section;
    }
}
