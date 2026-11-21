using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit.v3;

[assembly: TianWen.Lib.Tests.TestCostProbe]

namespace TianWen.Lib.Tests;

/// <summary>
/// What each test cost the process, written as the test ends, when <c>TIANWEN_TEST_COST_FILE</c> names a file: its wall time,
/// the bytes allocated, the collections of each generation and the time the collector held every thread. Off, and one
/// null check a test, without the variable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The counters are the process's</b>, so a line is one test's only in a SERIAL run (<c>--parallel none</c>): with four
/// tests at once each line also holds what the other three did in the same window, which is the question a second,
/// parallel, run answers (who pays for whose garbage). <c>tools/test-cost-report.py</c> reads the file.
/// </para>
/// <para>
/// Why it exists: the suite's time is summed test time, and a test's time is not only its own work. Every collection of
/// a workstation collector stops every thread, so one test that allocates gigabytes slows each test running beside it.
/// SharpAstro/Codecs measured that (its issue #92: half of one test's wall time in the full suite was collector pauses
/// caused by other tests) and cut its suite's allocation from 276 to 71 GiB with a probe like this one.
/// </para>
/// <para>
/// The window is the test method: xunit calls <see cref="Before"/> after the class is constructed and
/// <see cref="After"/> before it is disposed, so what a constructor or a fixture allocates lands on no line.
/// </para>
/// <para>
/// <b>The bytes are approximate</b> (<c>GC.GetTotalAllocatedBytes(precise: false)</c>): the runtime counts an
/// allocation context when a thread has used it up, some kilobytes at a time, so a small test's figure is not exact
/// and small tests are not to be ranked by it. It is the megabytes and gigabytes this is for.
/// </para>
/// <para>
/// <b>The file is appended to, never emptied</b>: a second run into the same file adds its lines after the first's.
/// Name a new file for a run or delete the old one. The report says when a NAME is on more than one line, and it
/// cannot tell which of two things that is: a second run appended to the file, or rows of one theory whose display
/// names xunit shortened to the same text (a string argument is cut at 50 characters). It sums that name's lines.
/// </para>
/// <para>
/// <b>What was allocated</b>, with <c>TIANWEN_TEST_COST_TYPES=1</c> as well: the runtime's allocation tick (one event
/// for about every 100 KB a thread allocates, naming the type of the object that crossed the mark) is summed per type
/// over the test, and the eight largest go to <c>&lt;cost file&gt;.types</c> as test, type, sampled bytes. A sample, so
/// small figures are noise; it names the type and not the call site. Serial runs only, as above.
/// </para>
/// <para>
/// <b>How much memory a test needed</b>, with <c>TIANWEN_TEST_COST_PEAKS=1</c> as well: three more fields on its line,
/// in bytes. Before the test the heap is collected until it is stable (<c>GC.GetTotalMemory(true)</c>), so the first
/// is what the process held live as the test began: fixtures, caches, statics. From then on the heap in use and the
/// working set are read every 25 ms and once more as the test ends, and the highest of each are the second and the
/// third. The heap's peak less its start is what the test added: its live objects, and the garbage that was waiting
/// for a collection at that moment. The working set also holds what the heap does not (native buffers, code) and
/// memory the collector has freed and not yet handed back, so it follows the tests before this one too: read it for
/// how large the PROCESS got, and the heap for whose doing that was. Serial runs only. A collection before every test
/// moves when the collector runs, so take the collection counts and the pauses from a run without this.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestCostProbeAttribute : BeforeAfterTestAttribute
{
    /// <summary>The variable that names the file to append to.</summary>
    internal const string FileVariable = "TIANWEN_TEST_COST_FILE";

    private static readonly string? CostFile =
        Environment.GetEnvironmentVariable(FileVariable) is { Length: > 0 } named ? named : null;

    /// <summary>The variable that asks for the memory peaks, with the value <c>1</c>.</summary>
    internal const string PeaksVariable = "TIANWEN_TEST_COST_PEAKS";

    private static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(25);

    private static readonly ConcurrentDictionary<string, Running> Started = new();

    // Reads the process every SampleEvery, for as long as the process lives, when peaks are asked for. Declared after
    // Started and SampleEvery, which it uses: static initialisers run in the order they are written. A timer and not
    // a loop: nothing waits between two readings.
    private static readonly Timer? PeakSampling =
        CostFile is not null && Environment.GetEnvironmentVariable(PeaksVariable) == "1"
            ? new Timer(static _ => RaiseRunning(Environment.WorkingSet, GC.GetTotalMemory(forceFullCollection: false)),
                null, SampleEvery, SampleEvery)
            : null;

    private static readonly AllocationSampler? Sampler =
        CostFile is not null && Environment.GetEnvironmentVariable("TIANWEN_TEST_COST_TYPES") == "1" ? new AllocationSampler() : null;

    // One writer at a time on the file: tests end on several threads at once in a parallel run, and two appends would
    // interleave their lines. Never reached from a render thread; held for one small append.
    private static readonly Lock Writing = new();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (CostFile is not null)
        {
            // Peaks: the heap is collected until it is stable first, so the test starts from what the process holds
            // live (and the running class's own instance and fixtures) and what it adds is its own. Before the mark,
            // so these collections land on no line of the bytes file; the type sampler is cleared after them, so what
            // they allocate (finalizers of the previous test's objects) is not this test's .types lines either.
            var peaks = PeakSampling is null
                ? null
                : new Peaks(heapAtStart: GC.GetTotalMemory(forceFullCollection: true), workingSetAtStart: Environment.WorkingSet);
            Sampler?.Clear();
            Started[test.UniqueID] = new Running(Mark.Now(), peaks);
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (CostFile is null || !Started.TryRemove(test.UniqueID, out var running))
        {
            return;
        }

        var end = Mark.Now();
        // One more reading as the test ends: a test shorter than the sampler's step has no other.
        running.Peaks?.Raise(Environment.WorkingSet, GC.GetTotalMemory(forceFullCollection: false));
        var line = Line(test.TestDisplayName, running.Start, end, running.Peaks);
        var types = Sampler?.Largest(test.TestDisplayName, 8);
        lock (Writing)
        {
            File.AppendAllText(CostFile, line);
            if (types is not null)
            {
                File.AppendAllText(CostFile + ".types", types);
            }
        }
    }

    /// <summary>
    /// One tab-separated line: test, wall ms, bytes allocated, gen0, gen1 and gen2 collections, collector pause ms. With
    /// peaks, three more, in bytes: the heap at the test's start, the highest heap and the highest working set.
    /// </summary>
    internal static string Line(string test, Mark start, Mark end, Peaks? peaks = null)
    {
        var counters = string.Create(CultureInfo.InvariantCulture,
            $"{test}\t{Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds:F0}\t{end.Allocated - start.Allocated}\t{end.Gen0 - start.Gen0}\t{end.Gen1 - start.Gen1}\t{end.Gen2 - start.Gen2}\t{(end.Paused - start.Paused).TotalMilliseconds:F0}");
        return peaks is null
            ? counters + "\n"
            : string.Create(CultureInfo.InvariantCulture, $"{counters}\t{peaks.HeapAtStart}\t{peaks.Heap}\t{peaks.WorkingSet}\n");
    }

    /// <summary>One reading of the process, taken into every test running now.</summary>
    private static void RaiseRunning(long workingSet, long heap)
    {
        foreach (var running in Started)
        {
            running.Value.Peaks?.Raise(workingSet, heap);
        }
    }

    /// <summary>A test that has started: the counters as it began, and its peaks when they are asked for.</summary>
    private sealed record Running(Mark Start, Peaks? Peaks);

    /// <summary>
    /// How high the process's memory went while one test ran: the managed heap the test started from, and the highest
    /// heap and working set read from then on. A peak only rises.
    /// </summary>
    internal sealed class Peaks(long heapAtStart, long workingSetAtStart)
    {
        private long _heap = heapAtStart;
        private long _workingSet = workingSetAtStart;

        /// <summary>Bytes in managed objects as the test began, the heap having been collected until it was stable.</summary>
        public long HeapAtStart { get; } = heapAtStart;

        /// <summary>The most bytes in managed objects at any reading, garbage not yet collected included.</summary>
        public long Heap => Interlocked.Read(ref _heap);

        /// <summary>The largest working set of the process at any reading.</summary>
        public long WorkingSet => Interlocked.Read(ref _workingSet);

        /// <summary>Takes one reading. The sampler's thread and the test's own both call this, so each peak is raised by compare-and-swap.</summary>
        public void Raise(long workingSet, long heap)
        {
            RaiseTo(ref _workingSet, workingSet);
            RaiseTo(ref _heap, heap);
        }

        private static void RaiseTo(ref long peak, long reading)
        {
            // Ends once the reading is no longer above the peak: a lost swap means another reading raised it meanwhile.
            for (var seen = Interlocked.Read(ref peak); reading > seen;)
            {
                var was = Interlocked.CompareExchange(ref peak, reading, seen);
                if (was == seen)
                {
                    return;
                }

                seen = was;
            }
        }
    }

    /// <summary>Sums the runtime's allocation ticks per type. Its own boxing shows up in the sample; it is small beside what it finds.</summary>
    private sealed class AllocationSampler : EventListener
    {
        private const EventKeywords GcKeyword = (EventKeywords)0x1;

        // EventListener's constructor calls OnEventSourceCreated, which turns the runtime's events on, so an allocation
        // tick can reach OnEventWritten before this class's own constructor body runs. That is safe for a field with
        // an initialiser: C# runs a class's field initialisers BEFORE its base constructor, so this is set by then.
        // Keep it an initialiser; assigned in a constructor body it would be null in that window.
        private readonly ConcurrentDictionary<string, long> _bytes = new();

        public void Clear() => _bytes.Clear();

        public string Largest(string test, int count) => string.Concat(_bytes
            .OrderByDescending(static kv => kv.Value)
            .Take(count)
            .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{test}\t{kv.Key}\t{kv.Value}\n")));

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal)
                || eventData.Payload is not { } payload || eventData.PayloadNames is not { } names)
            {
                return;
            }

            var type = names.IndexOf("TypeName");
            var amount = names.IndexOf("AllocationAmount64");
            if (type >= 0 && amount >= 0 && payload[type] is string typeName && payload[amount] is ulong bytes)
            {
                _bytes.AddOrUpdate(typeName, (long)bytes, (_, sum) => sum + (long)bytes);
            }
        }
    }

    /// <summary>The process's counters at one instant.</summary>
    internal readonly record struct Mark(long Timestamp, long Allocated, int Gen0, int Gen1, int Gen2, TimeSpan Paused)
    {
        public static Mark Now() => new Mark(
            Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
    }
}
