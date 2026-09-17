using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The policy for showing object pictures on a surface that draws frames: when to start a load, when to adopt
/// its result, what to remember about a failure, and how many pictures to keep. Each host supplies only what
/// is its own: how a picture is loaded, how a loaded one becomes something it can draw, and how that is freed
/// (a Vulkan texture from the desktop store, a WebGL texture from the browser's fetch).
/// </summary>
/// <remarks>
/// <para><b>Render-thread only.</b> <see cref="TryGet"/> is called from the frame that draws the panel; the
/// load runs wherever its task runs, and its result crosses back through the <see cref="Task"/> itself, polled
/// here, so no field is shared between threads.</para>
/// <para><b>A failure is remembered</b> for <see cref="RetryAfter"/>: the panel asks every frame, and a picture
/// that cannot be had must not become a request per frame (the lesson the comet apparition fetch paid for). A
/// faulted load and a load that came back empty are the same answer here, and the faulted one is LOGGED here,
/// because this is the only place that sees it: the load's own exception is what it faults with (a DNS
/// failure, a timeout, a decode error on a cached file), and nothing downstream is handed it. A load that
/// came back empty logged its own reason where it decided (the store's refused status).</para>
/// <para><b>Few pictures are kept.</b> A panel shows one at a time, so the cache holds the
/// <see cref="Capacity"/> most recently drawn and releases the rest.</para>
/// </remarks>
/// <typeparam name="TLoaded">What a load produces, off the render thread.</typeparam>
/// <typeparam name="THandle">What the host draws with, made from a <typeparamref name="TLoaded"/> on the render thread.</typeparam>
public sealed class ObjectPictureCache<TLoaded, THandle>(
    Func<ObjectArticleImage, int, Task<TLoaded?>> load,
    Func<TLoaded, THandle> adopt,
    Action<THandle> release,
    Action requestRedraw,
    TimeProvider? clock = null,
    ILogger? logger = null)
    where TLoaded : class
{
    /// <summary>How long a picture that could not be had is left alone before it is asked for again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>How many pictures are kept ready to draw.</summary>
    public const int Capacity = 6;

    private sealed class Slot
    {
        public Task<TLoaded?>? Load;
        public THandle? Handle;
        public bool HasHandle;
        public long FailedAt;
        public long LastDrawn;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>How many loads have been started, for tests: a picture asked for every frame is still one load.</summary>
    internal int LoadsStarted { get; private set; }

    /// <summary>
    /// The drawable picture for <paramref name="image"/> at the standard width covering <paramref name="pixels"/>,
    /// when it is in hand. When it is not, starts or polls its load and answers false; the host draws the empty
    /// slot this frame, and <c>requestRedraw</c> brings the frame that adopts the result.
    /// </summary>
    public bool TryGet(in ObjectArticleImage image, int pixels, [MaybeNullWhen(false)] out THandle handle)
    {
        var key = image.ThumbnailUrl(pixels);
        var now = _clock.GetTimestamp();
        if (!_slots.TryGetValue(key, out var slot))
        {
            _slots[key] = slot = new Slot();
        }
        slot.LastDrawn = now;

        if (slot.Load is { IsCompleted: true } finished)
        {
            slot.Load = null;
            if (finished.IsCompletedSuccessfully && finished.Result is { } loaded)
            {
                slot.Handle = adopt(loaded);
                slot.HasHandle = true;
                EvictBeyondCapacity();
            }
            else
            {
                // Reading the exception is what makes it observed, so it never resurfaces as an unobserved-task
                // warning at finalisation; logging it is what makes the failure visible at all. A timeout is a
                // cancellation to the task and is logged the same, since nothing here asked for one.
                if (finished.Exception is { } failure)
                {
                    logger?.LogWarning(failure.InnerException ?? failure,
                        "Object picture {Url} could not be loaded; not asked for again for {RetryAfter}", key, RetryAfter);
                }
                slot.FailedAt = now;
            }
        }

        if (slot.HasHandle && slot.Handle is { } ready)
        {
            handle = ready;
            return true;
        }

        if (slot.Load is null && (slot.FailedAt == 0 || _clock.GetElapsedTime(slot.FailedAt, now) >= RetryAfter))
        {
            slot.FailedAt = 0;
            LoadsStarted++;
            slot.Load = load(image, pixels);
            // The frame that adopts it has to come: nothing else redraws a panel the pointer is resting on.
            _ = slot.Load.ContinueWith(_ => requestRedraw(), TaskScheduler.Default);
        }

        handle = default;
        return false;
    }

    /// <summary>The largest rect of the picture's aspect ratio inside <paramref name="slot"/>, centred in it.</summary>
    public static RectF32 Fit(RectF32 slot, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return slot;
        }

        var scale = MathF.Min(slot.Width / width, slot.Height / height);
        var w = width * scale;
        var h = height * scale;
        return new RectF32(slot.X + ((slot.Width - w) / 2f), slot.Y + ((slot.Height - h) / 2f), w, h);
    }

    /// <summary>Releases every picture this cache holds.</summary>
    public void Clear()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.HasHandle && slot.Handle is { } handle)
            {
                release(handle);
            }
        }
        _slots.Clear();
    }

    private void EvictBeyondCapacity()
    {
        while (true)
        {
            var held = 0;
            string? oldest = null;
            var oldestDrawn = long.MaxValue;
            foreach (var (key, slot) in _slots)
            {
                if (!slot.HasHandle)
                {
                    continue;
                }
                held++;
                if (slot.LastDrawn < oldestDrawn)
                {
                    oldest = key;
                    oldestDrawn = slot.LastDrawn;
                }
            }

            if (held <= Capacity || oldest is null)
            {
                return;
            }

            if (_slots[oldest].Handle is { } evicted)
            {
                release(evicted);
            }
            _slots.Remove(oldest);
        }
    }
}
