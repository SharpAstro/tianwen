using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The browser's sprite and picture quads (<see cref="FrameDrawBuffers{TBuffer}"/>, which <c>WebGlPlanetSprites</c> and
/// <c>WebGlObjectPictures</c> draw through) against a renderer that behaves as WebGl.Renderer does: an upload runs at once, a draw is
/// only RECORDED and replayed at Present. With one shared quad the atlas's Moon disc replayed at the info panel's place, so the
/// map showed no Moon at all while the panel showed its disc.
/// </summary>
public sealed class FrameDrawBuffersTests
{
    // WebGl.Renderer's semantics, reduced: CreateBuffer/UpdateBuffer write at once, DrawBuffer is an opcode, Present replays them.
    private sealed class DeferredRenderer
    {
        private readonly List<float[]> _buffers = [];
        private readonly List<int> _draws = [];

        public int Create(float[] data)
        {
            _buffers.Add([.. data]);
            return _buffers.Count - 1;
        }

        public void Update(int buffer, float[] data) => _buffers[buffer] = [.. data];

        public void Draw(int buffer) => _draws.Add(buffer);

        public float[][] Present()
        {
            float[][] replayed = [.. _draws.Select(b => _buffers[b])];
            _draws.Clear();
            return replayed;
        }

        public int BufferCount => _buffers.Count;
    }

    [Fact]
    public void EveryReplayedDrawReadsItsOwnQuad()
    {
        var renderer = new DeferredRenderer();
        var quads = new FrameDrawBuffers<int>(renderer.Create, renderer.Update);
        // One array rewritten per draw, as the hosts' scratch quad is.
        var scratch = new float[2];

        // Three frames: the map's disc of the Moon, then the panel's disc of it, each at its own place.
        for (var frame = 0; frame < 3; frame++)
        {
            quads.BeginFrame();
            (float X, float Y)[] places = [(1281 + frame, 537), (335, 1131)];
            foreach (var (x, y) in places)
            {
                scratch[0] = x;
                scratch[1] = y;
                renderer.Draw(quads.Write(scratch));
            }

            var replayed = renderer.Present();
            replayed.Length.ShouldBe(places.Length);
            for (var i = 0; i < places.Length; i++)
            {
                replayed[i][0].ShouldBe(places[i].X, $"draw {i} of frame {frame} replayed at another draw's place");
                replayed[i][1].ShouldBe(places[i].Y, $"draw {i} of frame {frame} replayed at another draw's place");
            }
        }

        // A frame costs a rewrite of each buffer, never a new one.
        renderer.BufferCount.ShouldBe(2);
    }
}
