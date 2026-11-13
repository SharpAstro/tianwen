using System;
using System.Collections.Generic;

namespace TianWen.UI.Abstractions;

/// <summary>
/// One buffer per DRAW of a frame, for a host whose uploads run at once but whose draws are RECORDED and replayed later (WebGL:
/// <c>WebGlRenderer.UpdateBuffer</c> executes immediately, a draw emits an opcode replayed at Present). A single buffer rewritten for
/// each draw leaves every draw of the frame reading the LAST draw's data: the atlas's Moon disc was drawn in the info panel's slot,
/// where the panel's own disc of it went, and so vanished from the map. The <c>n</c>th draw of a frame takes the <c>n</c>th buffer,
/// reused frame after frame; <see cref="BeginFrame"/> starts the count again. <see cref="PlanetBodyInstances{TBuffer}"/> is the same
/// rule for the orrery's ray-traced bodies.
/// </summary>
/// <param name="create">Creates a buffer holding the given floats.</param>
/// <param name="update">Replaces a buffer's floats.</param>
public sealed class FrameDrawBuffers<TBuffer>(Func<float[], TBuffer> create, Action<TBuffer, float[]> update)
{
    private readonly List<TBuffer> _buffers = [];
    private int _drawnThisFrame;

    /// <summary>The buffers made, one per draw of the busiest frame so far.</summary>
    public IReadOnlyList<TBuffer> Buffers => _buffers;

    /// <summary>A new frame: the next draw takes the first buffer again.</summary>
    public void BeginFrame() => _drawnThisFrame = 0;

    /// <summary>Writes <paramref name="data"/> into this draw's own buffer and returns it, which the draw reads.</summary>
    public TBuffer Write(float[] data)
    {
        var nth = _drawnThisFrame++;
        if (nth < _buffers.Count)
        {
            update(_buffers[nth], data);
            return _buffers[nth];
        }
        var created = create(data);
        _buffers.Add(created);
        return created;
    }
}
