using System;
using System.IO;
using System.Linq;
using System.Threading;
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
public class SharedAppDataFileTests(ITestOutputHelper output)
{
    private const int WritesPerWriter = 1000;

    // Reads that must happen WHILE the writers write: on a fast runner (Linux, no real-time scanner) 2,000 writes
    // took 0.8 s and finished before the reader had read once, so the test proved nothing and failed its premise.
    private const int ReadsBeside = 20;

    [Fact(Timeout = 300_000)]
    public async Task TwoWritersOfOneProfileBesideItsReadersLoseNothingAndNeverFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, new DirectoryInfo(Directory.CreateTempSubdirectory("shared-appdata-").FullName));
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
        var path = Path.Combine(Directory.CreateTempSubdirectory("shared-appdata-").FullName, "record.json");
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
        var path = Path.Combine(Directory.CreateTempSubdirectory("shared-appdata-").FullName, "record.json");
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
        var external = new FakeExternal(output, new DirectoryInfo(Directory.CreateTempSubdirectory("shared-appdata-").FullName));
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
