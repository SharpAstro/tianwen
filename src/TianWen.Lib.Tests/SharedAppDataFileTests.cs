using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// One AppData file with two writers and readers beside them, which is what the GUI, the TUI and a local server
/// make of every profile (P0c item 3 of docs/plans/hardware-in-the-server.md, #788). Two writers in one process
/// stand in for two processes: Windows decides file sharing per HANDLE, so they collide the same way.
/// </summary>
public class SharedAppDataFileTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private const int WritesPerWriter = 1000;

    // Reads that must happen WHILE the writers write: on a fast runner (Linux, no real-time scanner) 2,000 writes
    // took 0.8 s and finished before the reader had read once, so the test proved nothing and failed its premise.
    private const int ReadsBeside = 20;

    [Fact(Timeout = 300_000)]
    public async Task TwoWritersOfOneProfileBesideItsReadersLoseNothingAndNeverFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, _folders.Create("shared-appdata-"));
        IExternal shared = external;
        var profileId = Guid.NewGuid();
        var seed = Version("seed", 0);
        await seed.SaveAsync(external, ct);
        var path = seed.ProfileFullPath(external).FullName;

        // The server's limit watcher lists every profile every 5 s; the planner, session and weather records
        // are read through the JSON helper.
        var log = new RecordingLogger<ProfileIterator>();
        var iterator = new ProfileIterator(external, log);
        using var writing = new CancellationTokenSource();
        var reads = 0;
        var missed = new System.Collections.Generic.List<string>();
        var readers = Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested)
            {
                await iterator.DiscoverAsync(ct);
                if (iterator.RegisteredDevices(DeviceType.Profile).Count() is var listed && listed != 1)
                {
                    missed.Add($"read {reads}: the profile iterator found {listed}; logged [{log.Drain()}]");
                }

                if (await shared.TryReadJsonAsync(path, Profile.ProfileJsonSerializerContextIndented.ProfileDto, log, ct) is null)
                {
                    missed.Add($"read {reads}: the JSON read found nothing; logged [{log.Drain()}]");
                }

                Interlocked.Increment(ref reads);
            }
        }, ct);

        int lastA, lastB;
        try
        {
            var a = Task.Run(() => WriteAsync("a"), ct);
            var b = Task.Run(() => WriteAsync("b"), ct);
            lastA = await a;
            lastB = await b;
        }
        finally
        {
            await writing.CancelAsync();
            await readers;
        }

        output.WriteLine($"{reads} reads beside {lastA + lastB} writes");
        reads.ShouldBeGreaterThanOrEqualTo(ReadsBeside, "premise: the readers ran beside the writers");
        missed.ShouldBeEmpty("a reader found the profile gone or unreadable while it was being replaced");
        var last = await shared.TryReadJsonAsync(path, Profile.ProfileJsonSerializerContextIndented.ProfileDto, ct: ct);
        last.ShouldNotBeNull().Name.ShouldBeOneOf($"a {lastA}", $"b {lastB}");
        Directory.GetFiles(external.ProfileFolder.FullName, "*.tmp").ShouldBeEmpty("a write left its staging file behind");

        Profile Version(string writer, int n) => new Profile(profileId, $"{writer} {n}", ProfileData.Empty);

        // At least WritesPerWriter each, and on until the readers have read beside them ReadsBeside times.
        async Task<int> WriteAsync(string writer)
        {
            var n = 0;
            while (n < WritesPerWriter || Volatile.Read(ref reads) < ReadsBeside)
            {
                await Version(writer, ++n).SaveAsync(external, ct);
            }
            return n;
        }
    }

    /// <summary>
    /// A reader may hold a file for longer than a writer's retries last (a slow parse, a process paused in a
    /// debugger). Windows refuses to replace a file some handle holds without delete sharing, so the reader's
    /// sharing is what lets the write through at all; the retry only rides out the moment two renames meet.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AWriteReplacesAFileAReaderIsStillHolding()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("shared-appdata-").FullName, "record.json");
        await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("old"u8.ToArray(), token).AsTask(), ct);

        await using (var held = await SharedFile.OpenReadAsync(path, ct))
        {
            await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), ct);

            using var reader = new StreamReader(held);
            (await reader.ReadToEndAsync(ct)).ShouldBe("old", "the reader keeps the version it opened");
        }

        (await File.ReadAllTextAsync(path, ct)).ShouldBe("new");
    }

    /// <summary>
    /// A path past 260 characters, from a host that declares no long paths (a test's, like a machine without the
    /// setting): a data root in a deep folder put every profile write there past it, and the rename's raw
    /// <c>CreateFileW</c> refused them all with "path not found" while .NET wrote the staging file beside it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AWriteReplacesAFileWhosePathIsLongerThan260Characters()
    {
        var ct = TestContext.Current.CancellationToken;
        var deep = Path.Combine(_folders.Create("shared-appdata-").FullName, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(deep);
        var path = Path.Combine(deep, $"{Guid.NewGuid()}.json");
        path.Length.ShouldBeGreaterThan(260);
        await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("old"u8.ToArray(), token).AsTask(), ct);

        await using (var held = await SharedFile.OpenReadAsync(path, ct))
        {
            await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), ct);

            using var reader = new StreamReader(held);
            (await reader.ReadToEndAsync(ct)).ShouldBe("old", "the reader keeps the version it opened");
        }

        (await File.ReadAllTextAsync(path, ct)).ShouldBe("new");
    }

    /// <summary>
    /// Another program (a scanner, a backup, an older TianWen) may hold the file WITHOUT delete sharing, and even
    /// the POSIX rename is refused while it does. The write waits that out for a bounded time instead of failing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AWriteWaitsOutAHandleThatDoesNotShareDelete()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Unix has no mandatory file sharing, so nothing refuses the rename");
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("shared-appdata-").FullName, "record.json");
        await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("old"u8.ToArray(), token).AsTask(), ct);

        Task write;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            write = SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), ct);
            await Task.Delay(50, ct);
            write.IsCompleted.ShouldBeFalse("premise: the replace is refused while the handle is open");
        }

        await write;
        (await File.ReadAllTextAsync(path, ct)).ShouldBe("new");
    }

    // The next three tests ran on the wall clock until #2191: the replace took a real 300 ms on a thread of its own and
    // the reader's deadline was a real second, and on a loaded runner the thread got its turn after the second had
    // passed, twice (the test took 1.7 s and 2.1 s where a pass took 0.3). The reader then called the name gone, rightly,
    // in a test that says it must not. Now the reader measures and sleeps on the clock it is given, a fake one here, and
    // says when it is about to wait; the replace is made from that hook, at a wait of the reader's own. The files and
    // the looks at them are real. Nothing is raced, and the orphan test no longer spends a real second.
    //
    // The replace is finished at the reader's 30th wait, 150 ms of its clock in: past the 130 ms the old fixed looks
    // spanned (now, +5, +25, +100 ms), so a reader that counted looks instead of watching the staging file fails here.
    private const int WaitsBeforeTheReplaceEnds = 30;

    private static Action ReplaceAtWait(int wait, string staging, string path, StrongBox<int> waits)
        => () =>
        {
            if (++waits.Value == wait)
            {
                File.Move(staging, path);
            }
        };

    /// <summary>
    /// The name is absent while a staging file sits beside it, which is a replace under way: the reader keeps
    /// looking for as long as that lasts instead of counting a fixed number of looks.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AReaderDoesNotBelieveANameGoneWhileAReplaceIsInProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("shared-appdata-").FullName, "record.json");
        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(staging, "new", ct);
        var clock = new FakeTimeProviderWrapper();
        var started = clock.GetUtcNow();
        var waits = new StrongBox<int>();

        await using var stream = await SharedFile.TryOpenReadAsync(path, clock, ReplaceAtWait(WaitsBeforeTheReplaceEnds, staging, path, waits), ct)
            ?? throw new Xunit.Sdk.XunitException("the reader called the name gone during a replace");

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(ct)).ShouldBe("new");
        waits.Value.ShouldBe(WaitsBeforeTheReplaceEnds, "it waited until the replace was done, and not once more");
        (clock.GetUtcNow() - started).ShouldBe(TimeSpan.FromMilliseconds(150), "longer than the 130 ms the old fixed looks spanned");
    }

    /// <summary>The same for a listing that expects a file: a replace under way is waited out.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AListingDoesNotBelieveAnExpectedFileGoneWhileAReplaceIsInProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("shared-appdata-");
        var path = Path.Combine(folder.FullName, "one.json");
        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(staging, "new", ct);
        var clock = new FakeTimeProviderWrapper();
        var started = clock.GetUtcNow();
        var waits = new StrongBox<int>();

        var (listed, looks) = await SharedFile.ListCountingLooksAsync(folder, ".json", [path], ct, clock,
            ReplaceAtWait(WaitsBeforeTheReplaceEnds, staging, path, waits));

        listed.Select(f => f.FullName).ShouldBe([path]);
        looks.ShouldBe(WaitsBeforeTheReplaceEnds + 1, "one look before each wait, and the one that found the file");
        (clock.GetUtcNow() - started).ShouldBe(TimeSpan.FromMilliseconds(150));
    }

    /// <summary>A file deleted for good has no replace beside it, so it is reported absent. (That it is answered without the deadline is not pinned here: a wall-clock bar is opt-in.)</summary>
    [Fact(Timeout = 60_000)]
    public async Task AFileDeletedForGoodIsReportedAbsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("shared-appdata-");
        var path = Path.Combine(folder.FullName, "record.json");
        await SharedFile.WriteAsync(path, (stream, token) => stream.WriteAsync("old"u8.ToArray(), token).AsTask(), ct);
        File.Delete(path);

        (await SharedFile.TryOpenReadAsync(path, ct)).ShouldBeNull();
        (await SharedFile.ListAsync(folder, ".json", [path], ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// A listing asks whether the names it MISSES are being replaced, not whether anything in the folder is: a
    /// crashed writer's staging file for another name is no reason to wait for these (#2186). No clock in this one:
    /// it asks the rule itself.
    /// </summary>
    [Fact]
    public void AStagingFileOfAnotherNameIsNotAReplaceOfTheNamesAListingMisses()
    {
        var folder = _folders.Create("shared-appdata-");
        var missing = Path.Combine(folder.FullName, "record.json");
        var other = Path.Combine(folder.FullName, "other.json");
        File.WriteAllText($"{other}.{Guid.NewGuid():N}.tmp", "orphan");

        SharedFile.AnyBeingReplaced(folder.FullName, [missing]).ShouldBeFalse("the only staging file here is another name's");
        SharedFile.AnyBeingReplaced(folder.FullName, [missing, other]).ShouldBeTrue("and it does count for the name it belongs to");

        File.WriteAllText($"{missing}.{Guid.NewGuid():N}.tmp", "new");
        SharedFile.AnyBeingReplaced(folder.FullName, [missing]).ShouldBeTrue();
    }

    /// <summary>
    /// The same rule at the listing itself (pilot's review of #2307: the test above would still pass with the
    /// folder-wide look put back in <c>ListAsync</c>). An expected file is gone for good and another name's orphaned
    /// staging file sits beside it: the listing believes the absence after its quiet looks. With the folder-wide look
    /// it took the orphan for a replace under way and looked again every few milliseconds for the whole deadline.
    /// Counted in looks and not in time: a wall-clock bar is opt-in.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AListingDoesNotWaitOnAnotherNamesStagingFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("shared-appdata-");
        var gone = Path.Combine(folder.FullName, "record.json");
        File.WriteAllText($"{Path.Combine(folder.FullName, "other.json")}.{Guid.NewGuid():N}.tmp", "orphan");

        var (files, looks) = await SharedFile.ListCountingLooksAsync(folder, ".json", [gone], ct);

        files.ShouldBeEmpty();
        looks.ShouldBe(SharedFile.QuietLooksNeeded + 1, "its quiet looks and the last look at the names, not a look every 5 ms until the deadline");
    }

    /// <summary>
    /// A staging file nobody will ever rename (a crashed writer's) does not keep a name alive for ever: the
    /// deadline ends the wait and the name is reported absent.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnOrphanedStagingFileEndsAtTheDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("shared-appdata-").FullName, "record.json");
        await File.WriteAllTextAsync($"{path}.{Guid.NewGuid():N}.tmp", "orphan", ct);
        var clock = new FakeTimeProviderWrapper();
        var started = clock.GetUtcNow();
        var waits = new StrongBox<int>();

        (await SharedFile.TryOpenReadAsync(path, clock, () => waits.Value++, ct)).ShouldBeNull();

        // The deadline itself, on the reader's clock: it gave the staging file its whole second, in 5 ms looks, and no more.
        (clock.GetUtcNow() - started).ShouldBe(SharedFile.AbsenceDeadline);
        waits.Value.ShouldBe(200);
    }

    /// <summary>
    /// A file every writer ADDS to, as the comet apparition cache is: each writer reads what is there, adds
    /// its own and writes the whole back, so without the file's lock across all three a write lands between
    /// another writer's read and write and one of them loses what it added.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TwoWritersAddingToOneFileBothKeepEverythingTheyAdded()
    {
        const int AddsPerWriter = 200;
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, _folders.Create("shared-appdata-"));
        IExternal shared = external;
        var path = Path.Combine(external.AppDataFolder.FullName, "added.json");

        var a = Task.Run(() => AddAsync("a"), ct);
        var b = Task.Run(() => AddAsync("b"), ct);
        await Task.WhenAll(a, b);

        var all = await shared.TryReadJsonAsync(path, SharedFileTestJsonContext.Default.StringArray, ct: ct);
        all.ShouldNotBeNull().Length.ShouldBe(2 * AddsPerWriter, "a write landed between another writer's read and its write");

        async Task AddAsync(string writer)
        {
            for (var n = 1; n <= AddsPerWriter; n++)
            {
                var added = $"{writer} {n}";
                await shared.UpdateJsonAsync(path, SharedFileTestJsonContext.Default.StringArray, current => [.. current ?? [], added], ct: ct);
            }
        }
    }
}

/// <summary>Keeps what a reader logged, so a miss says why it missed.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new System.Collections.Concurrent.ConcurrentQueue<string>();

    public string Drain()
    {
        var drained = new System.Collections.Generic.List<string>();
        while (_entries.TryDequeue(out var entry))
        {
            drained.Add(entry);
        }
        return string.Join(" | ", drained);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue($"{logLevel}: {formatter(state, exception)} ({exception?.GetType().Name}: {exception?.Message})");
}

[JsonSerializable(typeof(string[]))]
internal partial class SharedFileTestJsonContext : JsonSerializerContext;
