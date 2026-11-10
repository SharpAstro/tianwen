using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Grading;

public static partial class LightGrading
{
    /// <summary>
    /// What a night, rig and object's self-fit is made from (#2069): its samples in time order, and a fingerprint of them. Built with a
    /// snapshot's baselines (a pass over the table, no fit); the fit itself is started only through <see cref="SelfFitMemo"/>.
    /// </summary>
    private readonly record struct SelfFitInput(List<SkySample> Ordered, long Fingerprint)
    {
        public static SelfFitInput Of(List<SkySample> ordered)
        {
            var hash = new HashCode();
            foreach (var sample in ordered)
            {
                hash.Add(sample);
            }
            return new SelfFitInput(ordered, ((long)ordered.Count << 32) | (uint)hash.ToHashCode());
        }
    }

    /// <summary>
    /// The self-fits of every object-night asked for, OFF the thread that asks (#2069). A fit is 39 to 298 ms for 300 to 2000 frames
    /// (docs/measurements/sky-self-fit-cost.md), and every recorded grade is a new snapshot, so a fit per snapshot on the render
    /// thread was a stall per grade. Here the fit runs on the pool, is reused by every snapshot whose samples for the object-night are
    /// unchanged, and a changed set refits only once the one in flight has landed, so a folder load cannot stack fits.
    /// </summary>
    /// <remarks>
    /// A task is the hand-off (a completed task is read, never waited for), so no lock: an entry is a cold task published through a
    /// compare-and-swap, and only the winner starts it, so a racing loser's allocation is discarded and never runs the fit. While a
    /// refit is in flight the previous completed model is read (a fit of fewer frames of the same night); a first fit reads no term.
    /// </remarks>
    private static class SelfFitMemo
    {
        /// <summary>Entries kept; past it the oldest completed one goes, so an old folder's object-nights do not live for ever.</summary>
        public const int MaxEntries = 256;

        private static readonly ConcurrentDictionary<ObjectNight, Entry> _entries = new();
        private static readonly ConcurrentDictionary<string, int> _startsByObject = new();
        private static long _sequence;
        private static int _generation;

        /// <summary>Test seam: a fit for an object key starts only once its task completes.</summary>
        public static readonly ConcurrentDictionary<string, Task> StartHolds = new();

        /// <summary>Raised each time a fit lands (after its task completes), for a poll to repaint on.</summary>
        public static int Generation => Volatile.Read(ref _generation);

        public static int StartsFor(string objectKey) => _startsByObject.GetValueOrDefault(objectKey);

        public static Task LandedFor(string objectKey)
            => Task.WhenAll(_entries.Where(e => e.Key.ObjectKey == objectKey).Select(static e => e.Value.Landed));

        /// <summary>The model to read for <paramref name="key"/> now: the landed one for this fingerprint, else the previous one, else none.</summary>
        public static ExpectedSkyModel? Read(ObjectNight key, in SelfFitInput input)
        {
            _entries.TryGetValue(key, out var entry);
            if (entry is null || (entry.Fingerprint != input.Fingerprint && entry.Fit.IsCompleted))
            {
                var fresh = new Entry(input.Fingerprint, input.Ordered, entry?.Model, Interlocked.Increment(ref _sequence));
                var won = entry is null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, entry);
                if (won)
                {
                    _startsByObject.AddOrUpdate(key.ObjectKey, 1, static (_, n) => n + 1);
                    if (StartHolds.TryGetValue(key.ObjectKey, out var hold))
                    {
                        _ = hold.ContinueWith(_ => fresh.Fit.Start(TaskScheduler.Default), TaskScheduler.Default);
                    }
                    else
                    {
                        fresh.Fit.Start(TaskScheduler.Default);
                    }
                    Trim(fresh);
                    entry = fresh;
                }
                else
                {
                    _entries.TryGetValue(key, out entry);
                }
            }
            return entry?.Model;
        }

        private static void Trim(Entry keep)
        {
            if (_entries.Count <= MaxEntries)
            {
                return;
            }
            var oldest = _entries.Where(e => !ReferenceEquals(e.Value, keep) && e.Value.Fit.IsCompleted)
                .OrderBy(static e => e.Value.Sequence)
                .Take(_entries.Count - MaxEntries)
                .ToArray();
            foreach (var e in oldest)
            {
                _entries.TryRemove(e);
            }
        }

        private sealed class Entry
        {
            private readonly ExpectedSkyModel? _previous;

            public Entry(long fingerprint, List<SkySample> ordered, ExpectedSkyModel? previous, long sequence)
            {
                Fingerprint = fingerprint;
                Sequence = sequence;
                _previous = previous;
                // Cold on purpose: only the compare-and-swap's winner starts it.
                Fit = new Task<ExpectedSkyModel?>(() => ExpectedSkyModel.Fit(ordered));
                Landed = Fit.ContinueWith(static _ => Interlocked.Increment(ref _generation), TaskScheduler.Default);
            }

            public long Fingerprint { get; }
            public long Sequence { get; }
            public Task<ExpectedSkyModel?> Fit { get; }
            public Task Landed { get; }

            public ExpectedSkyModel? Model => Fit.IsCompletedSuccessfully ? Fit.Result : _previous;
        }
    }

    /// <summary>A count that rises each time a self-fit lands, so the viewer's frame loop can repaint the rows that now read a veil term.</summary>
    public static int SelfFitGeneration => SelfFitMemo.Generation;

    /// <summary>Test seam: how many self-fits were started for the object <paramref name="objectKey"/>, whatever its night.</summary>
    internal static int SelfFitsStartedFor(string objectKey) => SelfFitMemo.StartsFor(objectKey);

    /// <summary>Test seam: completes when every self-fit started so far for <paramref name="objectKey"/> has landed and been counted in <see cref="SelfFitGeneration"/>.</summary>
    internal static Task SelfFitsLandedFor(string objectKey) => SelfFitMemo.LandedFor(objectKey);

    /// <summary>Test seam: a fit for <paramref name="objectKey"/> starts only once <paramref name="hold"/> completes, so a test reads a PENDING fit with no wall-clock wait.</summary>
    internal static void HoldSelfFitsFor(string objectKey, Task hold) => SelfFitMemo.StartHolds[objectKey] = hold;
}
