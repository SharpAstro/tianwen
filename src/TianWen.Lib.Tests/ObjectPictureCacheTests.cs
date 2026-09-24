using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The policy both picture hosts share (<see cref="ObjectPictureCache{TLoaded, THandle}"/>): one load for a panel
/// that asks every frame, a failure left alone for a while, and only a few pictures kept.
/// </summary>
public sealed class ObjectPictureCacheTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed record Loaded(string Name);

    /// <summary>Keeps every entry logged, which is the one thing these tests ask of a log.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class Harness
    {
        public readonly ManualClock Clock = new ManualClock();
        public readonly Dictionary<string, TaskCompletionSource<Loaded?>> Pending = [];
        public readonly List<string> Released = [];
        public readonly TaskCompletionSource Redrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly RecordingLogger Log = new RecordingLogger();
        public readonly ObjectPictureCache<Loaded, string> Cache;

        public Harness()
        {
            Cache = new ObjectPictureCache<Loaded, string>(
                load: (image, _) =>
                {
                    var source = new TaskCompletionSource<Loaded?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Pending[image.FileName] = source;
                    return source.Task;
                },
                adopt: loaded => "handle:" + loaded.Name,
                release: Released.Add,
                requestRedraw: () => Redrawn.TrySetResult(),
                clock: Clock,
                logger: Log);
        }
    }

    private static ObjectArticleImage Image(string name)
        => new ObjectArticleImage(name + ".jpg", "ab", "CC0", "", "", AttributionRequired: false, Width: 400, Height: 300);

    [Fact]
    public void APanelAskingEveryFrameStartsOneLoad()
    {
        var harness = new Harness();
        var image = Image("crab");

        for (var frame = 0; frame < 5; frame++)
        {
            harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        }

        harness.Cache.LoadsStarted.ShouldBe(1);
    }

    [Fact]
    public async Task AFinishedLoadIsAdoptedOnTheNextAskAndTheFrameIsRequested()
    {
        var harness = new Harness();
        var image = Image("crab");
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();

        harness.Pending["crab.jpg"].SetResult(new Loaded("crab"));
        await harness.Redrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        harness.Cache.TryGet(in image, 330, out var handle).ShouldBeTrue();
        handle.ShouldBe("handle:crab");
        harness.Cache.TryGet(in image, 330, out _).ShouldBeTrue();
        harness.Cache.LoadsStarted.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedLoadIsLeftAloneUntilTheRetryInterval()
    {
        var harness = new Harness();
        var image = Image("crab");
        harness.Cache.TryGet(in image, 330, out _);
        harness.Pending["crab.jpg"].SetException(new InvalidOperationException("offline"));
        await harness.Redrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Clock.Advance(ObjectPictureCache<Loaded, string>.RetryAfter - TimeSpan.FromSeconds(1));
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Cache.LoadsStarted.ShouldBe(1, "a failure must not become a request per frame");

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Cache.LoadsStarted.ShouldBe(2, "after the interval the picture is asked for again");
    }

    [Fact]
    public async Task AFailedLoadIsLoggedWithItsOwnException()
    {
        // The cache is the only thing that ever sees why a load faulted; before this it read the exception
        // to keep the finaliser quiet and dropped it, so a dark picture frame had no line in any log.
        var harness = new Harness();
        var image = Image("crab");
        harness.Cache.TryGet(in image, 330, out _);
        harness.Pending["crab.jpg"].SetException(new InvalidOperationException("offline"));
        await harness.Redrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();

        var entry = harness.Log.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("offline");
        entry.Message.ShouldContain(image.ThumbnailUrl(330));

        // And once only: the frames that follow read the retry memory, not the task.
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Log.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ALoadThatCameBackEmptyIsAFailureToo()
    {
        var harness = new Harness();
        var image = Image("crab");
        harness.Cache.TryGet(in image, 330, out _);
        harness.Pending["crab.jpg"].SetResult(null);
        await harness.Redrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Cache.TryGet(in image, 330, out _).ShouldBeFalse();
        harness.Cache.LoadsStarted.ShouldBe(1);
    }

    [Fact]
    public async Task BeyondCapacityTheLeastRecentlyDrawnPictureIsReleased()
    {
        var harness = new Harness();
        var names = new List<string>();
        for (var i = 0; i <= ObjectPictureCache<Loaded, string>.Capacity; i++)
        {
            names.Add("object" + i);
        }

        foreach (var name in names)
        {
            var image = Image(name);
            harness.Cache.TryGet(in image, 330, out _);
            harness.Pending[name + ".jpg"].SetResult(new Loaded(name));
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await Task.WhenAll(harness.Pending.Values.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Adopt them in order; each ask stamps the picture as drawn now, so object0 is the oldest.
        foreach (var name in names)
        {
            var image = Image(name);
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            harness.Cache.TryGet(in image, 330, out _).ShouldBeTrue();
        }

        harness.Released.ShouldBe(["handle:object0"]);
    }

    [Theory]
    [InlineData(400, 200, 0f, 25f, 100f, 50f)]  // wider than the slot: fills the width, centred vertically
    [InlineData(100, 400, 37.5f, 0f, 25f, 100f)] // taller: fills the height, centred horizontally
    public void APictureIsFittedInsideItsSlotKeepingItsShape(int width, int height, float x, float y, float w, float h)
    {
        var fitted = ObjectPictureCache<Loaded, string>.Fit(new RectF32(0f, 0f, 100f, 100f), width, height);

        fitted.X.ShouldBe(x, 0.01f);
        fitted.Y.ShouldBe(y, 0.01f);
        fitted.Width.ShouldBe(w, 0.01f);
        fitted.Height.ShouldBe(h, 0.01f);
    }
}
