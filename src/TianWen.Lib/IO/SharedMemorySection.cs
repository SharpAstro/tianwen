using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TianWen.Lib.IO;

/// <summary>
/// A block of memory another process of this user on this machine maps by <see cref="Name"/>: the carrier of a frame a
/// local client reads without it crossing the socket (P4b of docs/plans/hardware-in-the-server.md, #932).
/// </summary>
/// <remarks>
/// <para><b>Windows:</b> a pagefile-backed section in the session's <c>Local\</c> namespace whose DACL grants the current
/// user alone (<c>CreateFileMappingW</c> with security attributes, since .NET's <c>CreateNew</c> takes none). It goes when
/// the last process holding it closes it, so a node that dies leaves nothing behind.</para>
/// <para><b>Linux and macOS:</b> .NET opens a map by name only on Windows, so the section is a file of mode 0600, under
/// <c>/dev/shm</c> (tmpfs, which is what <c>shm_open</c> uses) where there is one, else the directory the creator names,
/// mapped by both ends. Its creator unlinks it when disposed; a client that still maps it keeps its pages until it lets
/// go. A node that dies leaves its files, which the next node removes (<see cref="RemoveStale"/>).</para>
/// <para>The name is unguessable and travels only over the node's per-user socket.</para>
/// </remarks>
public sealed unsafe partial class SharedMemorySection : IDisposable
{
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly SafeHandle? _section;
    private readonly bool _unlinkOnDispose;
    private byte* _pointer;

    private SharedMemorySection(string name, long capacity, byte* pointer, MemoryMappedFile? map, MemoryMappedViewAccessor? view,
        SafeHandle? section, bool unlinkOnDispose)
    {
        Name = name;
        Capacity = capacity;
        _pointer = pointer;
        _map = map;
        _view = view;
        _section = section;
        _unlinkOnDispose = unlinkOnDispose;
    }

    /// <summary>What a client opens the section by: a Windows section's name, or a file's path elsewhere.</summary>
    public string Name { get; }

    /// <summary>How many bytes the section holds.</summary>
    public long Capacity { get; }

    /// <summary>
    /// Creates a section of <paramref name="capacity"/> bytes, zeroed, named after <paramref name="prefix"/> and a fresh
    /// GUID. <paramref name="unixDirectory"/> holds it where there is no <c>/dev/shm</c>.
    /// </summary>
    public static SharedMemorySection Create(long capacity, string prefix, string unixDirectory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var unique = $"{prefix}-{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            return CreateWindows($@"Local\{unique}", capacity);
        }

        var path = Path.Combine(UnixDirectory(unixDirectory), unique);
        var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        try
        {
            file.SetLength(capacity);
            return Mapped(path, capacity, MemoryMappedFile.CreateFromFile(file, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: false), MemoryMappedFileAccess.ReadWrite, unlinkOnDispose: true);
        }
        catch
        {
            file.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Opens the section <paramref name="name"/> names, <paramref name="capacity"/> bytes of it, to read.</summary>
    public static SharedMemorySection OpenForReading(string name, long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (OperatingSystem.IsWindows())
        {
            return OpenWindows(name, capacity);
        }

        var map = MemoryMappedFile.CreateFromFile(name, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        return Mapped(name, capacity, map, MemoryMappedFileAccess.Read, unlinkOnDispose: false);
    }

    /// <summary>
    /// Removes the sections a node that died left under <paramref name="prefix"/> (Linux and macOS; a Windows section goes
    /// with its last holder). For the node that holds the lock, as it starts: no other node writes under its prefix.
    /// </summary>
    /// <returns>How many it removed.</returns>
    public static int RemoveStale(string prefix, string unixDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }
        var directory = UnixDirectory(unixDirectory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "-*"))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException)
            {
                // Gone already, or not ours to remove.
            }
            catch (UnauthorizedAccessException)
            {
                // Another user's, under the same prefix: not ours.
            }
        }
        return removed;
    }

    /// <summary>The <paramref name="length"/> bytes at <paramref name="offset"/>.</summary>
    public Span<byte> Bytes(long offset, int length)
    {
        CheckRange(offset, length);
        return new Span<byte>(_pointer + offset, length);
    }

    /// <summary>The 64-bit value at <paramref name="offset"/>, which must be 8-byte aligned, to read and write in place.</summary>
    public ref long Int64At(long offset)
    {
        CheckRange(offset, sizeof(long));
        if (offset % sizeof(long) != 0)
        {
            throw new ArgumentException($"A 64-bit field at offset {offset} is not aligned", nameof(offset));
        }
        return ref Unsafe.AsRef<long>(_pointer + offset);
    }

    public void Dispose()
    {
        var pointer = _pointer;
        if (pointer is null)
        {
            return;
        }
        _pointer = null;
        if (_view is { } view)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
        }
        else if (_section is not null)
        {
            // A Windows section, mapped by address: unmapped before its handle closes.
            UnmapWindows(pointer);
        }
        _map?.Dispose();
        _section?.Dispose();
        if (_unlinkOnDispose)
        {
            try
            {
                File.Delete(Name);
            }
            catch (IOException)
            {
                // The next node removes it (RemoveStale).
            }
        }
    }

    private void CheckRange(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        if (offset < 0 || length < 0 || offset + length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Bytes {offset} to {offset + length} are outside the section's {Capacity}");
        }
    }

    private static string UnixDirectory(string fallback) => Directory.Exists("/dev/shm") ? "/dev/shm" : fallback;

    private static SharedMemorySection Mapped(string name, long capacity, MemoryMappedFile map, MemoryMappedFileAccess access, bool unlinkOnDispose)
    {
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = map.CreateViewAccessor(0, capacity, access);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            var section = new SharedMemorySection(name, capacity, pointer + view.PointerOffset, map, view, section: null, unlinkOnDispose);
            view = null;
            return section;
        }
        catch
        {
            view?.Dispose();
            map.Dispose();
            throw;
        }
    }
}
