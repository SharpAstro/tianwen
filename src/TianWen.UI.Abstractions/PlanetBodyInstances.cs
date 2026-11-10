using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The per-body <c>Body</c> blocks of <c>planet_body.frag</c> for a host whose uploads run at once but whose draws are RECORDED and
/// replayed later (WebGL: <c>WebGlRenderer.UpdateBuffer</c> executes immediately, <c>DrawInstanced</c> emits an opcode replayed at
/// Present). Each DRAW of a frame has a buffer of its OWN, holding its block as one instance, so no later draw of the frame can
/// overwrite what an earlier one reads: one shared uniform block drew every body with the last body's (review round 1 of PR #1589).
/// A body can be drawn more than once a frame, and is: the orrery's info panel draws the selected body's disc beside the orrery's own
/// (#1656), and with one buffer a body the panel's draw replaced the orrery's, so the selected planet vanished from its orbit. So a body
/// has a buffer per draw within a frame, reused frame after frame; <see cref="BeginFrame"/> starts the count again.
/// </summary>
/// <param name="create">Creates a buffer holding the given floats.</param>
/// <param name="update">Replaces a buffer's floats.</param>
public sealed class PlanetBodyInstances<TBuffer>(Func<float[], TBuffer> create, Action<TBuffer, float[]> update)
{
    private readonly Dictionary<CatalogIndex, List<(TBuffer Buffer, float[] Block)>> _bodies = [];
    private readonly Dictionary<CatalogIndex, int> _drawnThisFrame = [];

    /// <summary>The buffers made, one a body and draw of it within a frame: what a frame costs is a rewrite of each, never a new one.</summary>
    public IEnumerable<TBuffer> Buffers
    {
        get
        {
            foreach (var buffers in _bodies.Values)
            {
                foreach (var (buffer, _) in buffers)
                {
                    yield return buffer;
                }
            }
        }
    }

    /// <summary>A new frame: each body's next draw takes its first buffer again.</summary>
    public void BeginFrame() => _drawnThisFrame.Clear();

    /// <summary>
    /// Writes the block for <paramref name="view"/> into a buffer of its own for this draw of its body this frame, and returns that
    /// buffer, which its draw reads.
    /// </summary>
    public TBuffer Write(in PlanetView view, float x, float y, float radiusPx, float viewportWidth, float viewportHeight,
        (float R, float G, float B) ringAlbedo, RGBAColor32? nightTint, float opacity)
    {
        var nth = _drawnThisFrame.TryGetValue(view.Body, out var drawn) ? drawn : 0;
        _drawnThisFrame[view.Body] = nth + 1;
        if (!_bodies.TryGetValue(view.Body, out var buffers))
        {
            _bodies[view.Body] = buffers = [];
        }
        if (nth < buffers.Count)
        {
            var (buffer, block) = buffers[nth];
            PlanetBodyMap.WriteBlock(block, view, x, y, radiusPx, viewportWidth, viewportHeight, ringAlbedo, nightTint, opacity);
            update(buffer, block);
            return buffer;
        }
        var fresh = new float[PlanetBodyMap.BlockFloats];
        PlanetBodyMap.WriteBlock(fresh, view, x, y, radiusPx, viewportWidth, viewportHeight, ringAlbedo, nightTint, opacity);
        var created = create(fresh);
        buffers.Add((created, fresh));
        return created;
    }
}
