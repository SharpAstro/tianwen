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

    /// <summary>
    /// The selected body is drawn twice a frame, its orrery disc and its info panel's (#1656): each replayed draw must read its own
    /// block. With one buffer a body, the panel's write replaced the orrery's and the selected planet vanished from its orbit (seen in
    /// the browser, 2026-11-08). A second frame rewrites the same buffers and makes none.
    /// </summary>
    [Fact]
    public void ABodyDrawnTwiceInAFrameReadsEachDrawsOwnBlock()
    {
        var renderer = new DeferredRenderer();
        var instances = new PlanetBodyInstances<int>(renderer.Create, renderer.Update);
        var view = PlanetView.InBodyFrame(CatalogIndex.Saturn, Vector3.UnitY, Vector3.UnitX, new Vector3(1, 0, 0));
        var made = 0;

        for (var frame = 0; frame < 2; frame++)
        {
            instances.BeginFrame();
            // The orrery's disc on its orbit, then the panel's, larger, in the panel's corner.
            renderer.Draw(instances.Write(view, 1700 + frame, 900, 12, 2000, 1400, (0.5f, 0.5f, 0.5f), null, 1));
            renderer.Draw(instances.Write(view, 270, 1300 + frame, 60, 2000, 1400, (0.5f, 0.5f, 0.5f), null, 1));

            var replayed = renderer.Present();
            replayed.Length.ShouldBe(2);
            (replayed[0][0], replayed[0][2]).ShouldBe((1700f + frame, 12f), $"the orrery's Saturn (frame {frame}) read the panel's block");
            (replayed[1][1], replayed[1][2]).ShouldBe((1300f + frame, 60f), $"the panel's Saturn (frame {frame}) read the orrery's block");
            if (frame == 0)
            {
                made = instances.Buffers.Count();
            }
        }
        made.ShouldBe(2);
        instances.Buffers.Count().ShouldBe(made, "a second frame made buffers instead of rewriting its own");
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
