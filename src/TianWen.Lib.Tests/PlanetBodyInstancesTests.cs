using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The browser's orrery bodies (<see cref="PlanetBodyInstances{TBuffer}"/>, which <c>WebGlPlanetBodies</c> draws through) against a
/// renderer that behaves as WebGl.Renderer does: an upload runs at once, a draw is only RECORDED and replayed at Present. Every replayed
/// draw must read its own body's block; review round 1 of PR #1589 found every body drawn with the last body's.
/// </summary>
public sealed class PlanetBodyInstancesTests
{
    // WebGl.Renderer's semantics, reduced: UpdateBuffer/CreateBuffer write at once, DrawInstanced is an opcode, Present replays them.
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
    }

    [Fact]
    public void EveryReplayedDrawReadsItsOwnBodysBlock()
    {
        var renderer = new DeferredRenderer();
        var instances = new PlanetBodyInstances<int>(renderer.Create, renderer.Update);
        var bodies = new[] { CatalogIndex.Mars, CatalogIndex.Jupiter, CatalogIndex.Saturn };

        // Two frames, so a body's buffer is rewritten after a frame's Present as Play does.
        for (var frame = 0; frame < 2; frame++)
        {
            for (var i = 0; i < bodies.Length; i++)
            {
                var view = PlanetView.InBodyFrame(bodies[i], Vector3.UnitY, Vector3.UnitX, new Vector3(1, 0.2f * i, 0));
                renderer.Draw(instances.Write(view, 100 + (10 * i) + frame, 200, 5 + i, 900, 600, (0.5f, 0.5f, 0.5f), null, 1));
            }

            var replayed = renderer.Present();
            replayed.Length.ShouldBe(bodies.Length);
            for (var i = 0; i < bodies.Length; i++)
            {
                // The block's first vec4: the disc's centre x, y and radius, and its extent (Saturn's rings).
                replayed[i][0].ShouldBe(100 + (10 * i) + frame, $"{bodies[i]}'s draw (frame {frame}) read another body's centre");
                replayed[i][2].ShouldBe(5 + i, $"{bodies[i]}'s draw (frame {frame}) read another body's radius");
            }
        }
    }
}
