using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The per-body <c>Body</c> blocks of <c>planet_body.frag</c> for a host whose uploads run at once but whose draws are RECORDED and
/// replayed later (WebGL: <c>WebGlRenderer.UpdateBuffer</c> executes immediately, <c>DrawInstanced</c> emits an opcode replayed at
/// Present). Each body has a buffer of its OWN, holding its block as one instance, so no later draw of the frame can overwrite what an
/// earlier one reads: one shared uniform block drew every body with the last body's (review round 1 of PR #1589). A body is drawn at
/// most once a frame, which the orrery's loop guarantees.
/// </summary>
/// <param name="create">Creates a buffer holding the given floats.</param>
/// <param name="update">Replaces a buffer's floats.</param>
public sealed class PlanetBodyInstances<TBuffer>(Func<float[], TBuffer> create, Action<TBuffer, float[]> update)
{
    private readonly Dictionary<CatalogIndex, (TBuffer Buffer, float[] Block)> _bodies = [];

    /// <summary>The buffers made, one a body: what a frame costs is a rewrite of each, never a new one.</summary>
    public IEnumerable<TBuffer> Buffers
    {
        get
        {
            foreach (var (buffer, _) in _bodies.Values)
            {
                yield return buffer;
            }
        }
    }

    /// <summary>Writes the block for <paramref name="view"/> into its body's own buffer and returns that buffer, which its draw reads.</summary>
    public TBuffer Write(in PlanetView view, float x, float y, float radiusPx, float viewportWidth, float viewportHeight,
        (float R, float G, float B) ringAlbedo, RGBAColor32? nightTint, float opacity)
    {
        if (_bodies.TryGetValue(view.Body, out var entry))
        {
            PlanetBodyMap.WriteBlock(entry.Block, view, x, y, radiusPx, viewportWidth, viewportHeight, ringAlbedo, nightTint, opacity);
            update(entry.Buffer, entry.Block);
            return entry.Buffer;
        }
        var block = new float[PlanetBodyMap.BlockFloats];
        PlanetBodyMap.WriteBlock(block, view, x, y, radiusPx, viewportWidth, viewportHeight, ringAlbedo, nightTint, opacity);
        var buffer = create(block);
        _bodies[view.Body] = (buffer, block);
        return buffer;
    }
}
