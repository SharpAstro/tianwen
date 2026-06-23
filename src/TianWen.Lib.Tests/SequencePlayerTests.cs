using System.Collections.Generic;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Headless tests for <see cref="SequencePlayer"/> -- the off-render-thread, frame-paced playback
    /// driver for SER sequences. A <see cref="FakeSequenceSource"/> models the decode-ahead protocol with
    /// an explicit completion step (start -> background finishes via <c>CompleteDecode</c> -> publish), so
    /// pacing (decode-ahead + publish-when-due + idle-between-frames), single-decode gating, scrub
    /// coalescing, loop wraparound, and the still-image no-op can be asserted deterministically with no
    /// GPU and no real disk.
    /// </summary>
    public class SequencePlayerTests
    {
        /// <summary>
        /// Mimics a double-buffered <see cref="ISequencePlaybackSource"/>: <see cref="TryStartDecode"/>
        /// puts a frame "in flight"; <see cref="CompleteDecode"/> simulates the background decode
        /// finishing (then <see cref="IsDecodeReady"/> is true); <see cref="TryPublishDecoded"/> swaps it
        /// in. Starting a new decode supersedes a completed-but-unpublished one (the real source only
        /// gates on an in-flight decode, discarding a stale ready buffer by overwriting it).
        /// </summary>
        private sealed class FakeSequenceSource(int frameCount) : ISequencePlaybackSource
        {
            private int? _decoding;   // started, background not yet finished
            private int? _completed;  // finished, awaiting publish

            public int FrameCount => frameCount;
            public int FrameIndex { get; private set; }
            public bool IsDecoding => _decoding.HasValue;
            public bool IsDecodeReady => _completed.HasValue;
            public int StartedDecodes { get; private set; }

            public bool TryStartDecode(int index)
            {
                if (_decoding.HasValue || (uint)index >= (uint)frameCount || index == FrameIndex)
                {
                    return false;
                }

                _completed = null; // supersede any completed-but-unpublished decode
                _decoding = index;
                StartedDecodes++;
                return true;
            }

            /// <summary>Simulates the background decode finishing (the next publish will swap it in).</summary>
            public void CompleteDecode()
            {
                if (_decoding is { } d)
                {
                    _completed = d;
                    _decoding = null;
                }
            }

            public bool TryPublishDecoded(out int frameIndex)
            {
                if (_completed is { } c)
                {
                    _completed = null;
                    FrameIndex = c;
                    frameIndex = c;
                    return true;
                }

                frameIndex = FrameIndex;
                return false;
            }
        }

        private static ViewerState Playing(int frameCount, float fps) => new ViewerState
        {
            IsSequence = true,
            FrameCount = frameCount,
            FrameIndex = 0,
            IsPlaying = true,
            PlaybackFps = fps,
        };

        // Advances one displayed frame: completes the look-ahead decode, steps time past the next due
        // time, and ticks to publish. Returns the now-displayed frame index.
        private static int PumpFrame(SequencePlayer player, FakeSequenceSource src, ViewerState state, ref double t)
        {
            src.CompleteDecode();
            t += 1.0 / state.PlaybackFps + 0.001;
            player.Tick(src, state, t);
            return state.FrameIndex;
        }

        [Fact]
        public void Decodes_the_next_frame_ahead_then_publishes_when_due()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(600);
            var state = Playing(600, 30f);

            // First tick anchors the schedule and starts decoding the NEXT frame ahead (frame 1).
            player.Tick(src, state, 0.0).ShouldBeFalse();
            src.StartedDecodes.ShouldBe(1);
            src.IsDecoding.ShouldBeTrue();
            state.FrameIndex.ShouldBe(0);

            // Decode finishes; once the frame's display time arrives it is published.
            src.CompleteDecode();
            player.Tick(src, state, 0.04).ShouldBeTrue();
            state.FrameIndex.ShouldBe(1);
        }

        [Fact]
        public void Returns_false_between_frames_so_the_loop_idles()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(600);
            var state = Playing(600, 30f);

            player.Tick(src, state, 0.0);   // start decode-ahead of frame 1
            src.CompleteDecode();            // it's ready early...

            // ...but its display time (1/30s) has not arrived -> no publish, returns false (idle).
            player.Tick(src, state, 0.010).ShouldBeFalse();
            state.FrameIndex.ShouldBe(0);
        }

        [Fact]
        public void Only_one_decode_is_in_flight_at_a_time()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(600);
            var state = Playing(600, 30f);

            player.Tick(src, state, 0.0); // starts decode-ahead of frame 1
            src.IsDecoding.ShouldBeTrue();

            // Further ticks while that decode is still in flight must not start a second.
            player.Tick(src, state, 0.005);
            player.Tick(src, state, 0.010);
            src.StartedDecodes.ShouldBe(1);
        }

        [Fact]
        public void Plays_sequentially_and_loops_via_modulo_wraparound()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(3);
            var state = Playing(3, 30f);

            var t = 0.0;
            player.Tick(src, state, t); // anchor + start decode-ahead of frame 1

            var sequence = new List<int> { PumpFrame(player, src, state, ref t), PumpFrame(player, src, state, ref t), PumpFrame(player, src, state, ref t) };
            sequence.ShouldBe(new[] { 1, 2, 0 }); // 0 -> 1 -> 2 -> wraps to 0
        }

        [Fact]
        public void Step_while_paused_requests_and_decodes_one_frame()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(100);
            var state = new ViewerState { IsSequence = true, FrameCount = 100, IsPlaying = false };

            state.RequestedFrame = 5;
            player.Tick(src, state, 0.0); // consume request, start decode of 5
            state.RequestedFrame.ShouldBeNull();
            player.SeekPending.ShouldBeTrue();

            src.CompleteDecode();
            player.Tick(src, state, 0.0).ShouldBeTrue(); // publish frame 5
            state.FrameIndex.ShouldBe(5);
            player.SeekPending.ShouldBeFalse();
        }

        [Fact]
        public void Scrub_requests_coalesce_to_the_latest_target()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(100);
            var state = new ViewerState { IsSequence = true, FrameCount = 100, IsPlaying = false };

            // Scrub to 10 starts a decode; further requests arrive while it is in flight. They update the
            // pending target but cannot start their own decode (one-in-flight gate), so 25 and 40 are
            // skipped -- only the latest (55) decodes once the first completes.
            state.RequestedFrame = 10;
            player.Tick(src, state, 0.0);
            foreach (var target in new[] { 25, 40, 55 })
            {
                state.RequestedFrame = target;
                player.Tick(src, state, 0.0);
            }

            src.StartedDecodes.ShouldBe(1);

            src.CompleteDecode();
            player.Tick(src, state, 0.0); // ready frame 10 is not the target (55) -> supersede, decode 55
            src.CompleteDecode();
            player.Tick(src, state, 0.0); // publish 55

            state.FrameIndex.ShouldBe(55);
            src.StartedDecodes.ShouldBe(2); // only 10 and 55 ever decoded; 25 & 40 were coalesced away
            player.SeekPending.ShouldBeFalse();
        }

        [Fact]
        public void Still_image_is_a_noop()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(1);
            var state = new ViewerState { IsSequence = true, FrameCount = 1, IsPlaying = true };

            player.Tick(src, state, 0.0).ShouldBeFalse();
            player.Tick(src, state, 5.0).ShouldBeFalse();
            src.StartedDecodes.ShouldBe(0);
            state.FrameIndex.ShouldBe(0);
        }

        [Fact]
        public void Reset_abandons_a_pending_seek()
        {
            var player = new SequencePlayer();
            var src = new FakeSequenceSource(100);
            var state = new ViewerState { IsSequence = true, FrameCount = 100, IsPlaying = false };

            state.RequestedFrame = 42;
            player.Tick(src, state, 0.0); // start decode of 42
            src.StartedDecodes.ShouldBe(1);
            player.SeekPending.ShouldBeTrue();

            // Reset clears the player's seek/timing state (e.g. a new file opened). The abandoned decode
            // is never published, and the player chases nothing further.
            player.Reset();
            player.SeekPending.ShouldBeFalse();

            src.CompleteDecode();
            for (var i = 0; i < 5; i++)
            {
                player.Tick(src, state, 0.0);
            }

            state.FrameIndex.ShouldBe(0);    // seek abandoned -> never showed 42
            src.StartedDecodes.ShouldBe(1);  // no new work chased
        }
    }
}
