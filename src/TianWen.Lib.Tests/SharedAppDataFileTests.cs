using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
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
        var iterator = new ProfileIterator(external, NullLogger<ProfileIterator>.Instance);
        using var writing = new CancellationTokenSource();
        var reads = 0;
        var missed = 0;
        var readers = Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested)
            {
                await iterator.DiscoverAsync(ct);
                if (iterator.RegisteredDevices(DeviceType.Profile).Count() != 1)
                {
                    missed++;
                }

                if (await shared.TryReadJsonAsync(path, Profile.ProfileJsonSerializerContextIndented.ProfileDto, ct: ct) is null)
                {
                    missed++;
                }

                reads++;
            }
        }, ct);

        try
        {
            var a = Task.Run(() => WriteAsync("a"), ct);
            var b = Task.Run(() => WriteAsync("b"), ct);
            await Task.WhenAll(a, b);
        }
        finally
        {
            await writing.CancelAsync();
            await readers;
        }

        output.WriteLine($"{reads} reads beside {2 * WritesPerWriter} writes");
        reads.ShouldBeGreaterThan(0, "premise: the readers ran beside the writers");
        missed.ShouldBe(0, "a reader found the profile gone or unreadable while it was being replaced");
        var last = await shared.TryReadJsonAsync(path, Profile.ProfileJsonSerializerContextIndented.ProfileDto, ct: ct);
        last.ShouldNotBeNull().Name.ShouldBeOneOf($"a {WritesPerWriter}", $"b {WritesPerWriter}");
        Directory.GetFiles(external.ProfileFolder.FullName).ShouldHaveSingleItem("a write left its staging file behind");

        Profile Version(string writer, int n) => new Profile(profileId, $"{writer} {n}", ProfileData.Empty);

        async Task WriteAsync(string writer)
        {
            for (var n = 1; n <= WritesPerWriter; n++)
            {
                await Version(writer, n).SaveAsync(external, ct);
            }
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

[JsonSerializable(typeof(string[]))]
internal partial class SharedFileTestJsonContext : JsonSerializerContext;
