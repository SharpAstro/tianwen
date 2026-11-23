using System.Collections.Concurrent;
using System.Collections.Generic;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// One rectangle of the working planes that a routine of <see cref="ClassicalStarRemover"/> read or wrote: the ground it
/// was held to, what it asked for, and what it touched (#2378).
/// </summary>
/// <param name="Site">The routine, by name.</param>
/// <param name="Pass">Which of the build's tile passes it ran in, from 0; -1 outside them (calibration, a duplicate's
/// model put back, the masks, the report).</param>
/// <param name="Phase">The checkerboard phase of that pass, 0 to 3; -1 outside the passes.</param>
/// <param name="Ground">The ground of the tile the work is for (the tile grown by <c>MaxSubtractRadius</c> each side, cut
/// to the frame), or the whole frame for the work no tile does.</param>
/// <param name="Asked">What the routine reaches, cut to the frame alone, which is all that held it before #2378.</param>
/// <param name="Touched">The part of <paramref name="Asked"/> on the ground: the pixels it read or wrote.</param>
/// <param name="Write">Whether it wrote them.</param>
internal readonly record struct TileAccess(string Site, int Pass, int Phase, PixelRect Ground, PixelRect Asked, PixelRect Touched, bool Write);

/// <summary>
/// DEBUG-only: every rectangle of the working planes the classical star remover reads or writes, with the ground it was
/// held to (#2378). It is how the claim that two tiles of one phase never meet is checked rather than argued: a test
/// builds a plate with one and asserts that no touched rectangle leaves its ground, and the asked rectangles say where,
/// and how often, a tile's work reached past it.
/// </summary>
/// <remarks>
/// The remover's calls to it are compiled out of Release (<c>[Conditional("DEBUG")]</c>), so a Release build records
/// nothing at no cost; <see cref="IsActive"/> lets a test state that premise instead of asserting over nothing. Its
/// test carries <c>[Trait("Category", "DebugOnly")]</c>, which the Debug leg of <c>dotnet.yml</c> selects.
/// </remarks>
internal sealed class TileAccessRecorder
{
    /// <summary>Whether a build records anything: true in DEBUG, false in Release.</summary>
#if DEBUG
    internal const bool IsActive = true;
#else
    internal const bool IsActive = false;
#endif

    private readonly ConcurrentQueue<TileAccess> _accesses = new();

    /// <summary>What was recorded, in no particular order (the tiles of a phase record from their own threads).</summary>
    public IReadOnlyCollection<TileAccess> Accesses => _accesses;

    internal void Add(in TileAccess access)
    {
#if DEBUG
        _accesses.Enqueue(access);
#endif
    }
}
