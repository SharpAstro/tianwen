using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Where a project keeps the results of its stored steps: one FITS file a step, named by the step's key
/// (docs/plans/stacking-workbench.md, "The edit list"). A step whose key is here is not run again.
/// </summary>
/// <remarks>
/// <para><b>It is a cache.</b> Everything in it can be made again from the originals and the edit lists, so losing a
/// file costs a run and nothing else.</para>
/// <para><b>It knows what it wrote.</b> <c>index.json</c> lists every result with its key. A file that is in the
/// folder and not in the index is not the store's: it is never read as a result and never removed. Limits and
/// eviction (W11) work from the index for the same reason.</para>
/// <para><b>A result appears whole or not at all.</b> A step writes into a staging folder inside the store and the
/// file is renamed into place when the step has succeeded; the index gains its entry after that. A run that ends in
/// between leaves a file the index does not name, which the next run writes over.</para>
/// <para>One runner uses an instance at a time. Two programs on one store are safe: the index is updated under its
/// folder's lock (<see cref="SharedFile.UpdateJsonAsync"/>), and two runs of one step write the same file.</para>
/// </remarks>
public sealed class StepStore
{
    /// <summary>The store's folder inside a project's folder.</summary>
    public const string FolderName = "store";

    private const string IndexFileName = "index.json";
    private const string StagingFolderName = ".staging";

    private readonly Dictionary<string, StepStoreEntry> _entries;

    private StepStore(string folder, Dictionary<string, StepStoreEntry> entries)
    {
        Folder = folder;
        _entries = entries;
    }

    /// <summary>The store's folder. It is created by the first step that writes into it.</summary>
    public string Folder { get; }

    /// <summary>The name of the file a step of this key is stored as.</summary>
    public static string FileNameOf(string key) => FileNameOf(key, ".fits");

    /// <summary>The name of the file a step of this key is stored as, where it is no image: a list of results.</summary>
    public static string FileNameOf(string key, string ending) => DigestOf(key) + ending;

    // A key without the name of its algorithm: what a file of the store is named by.
    private static string DigestOf(string key) => key[(key.IndexOf(':') + 1)..];

    /// <summary>Opens the store in <paramref name="folder"/>, reading its index. Nothing is created: a folder that is
    /// not there is a store holding nothing.</summary>
    public static async Task<StepStore> OpenAsync(string folder, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(folder);
        var entries = new Dictionary<string, StepStoreEntry>(StringComparer.Ordinal);
        await using var stream = await SharedFile.TryOpenReadAsync(Path.Combine(full, IndexFileName), cancellationToken);
        if (stream is not null)
        {
            try
            {
                var index = await JsonSerializer.DeserializeAsync(stream, WorkbenchJsonContext.Default.StepStoreIndex, cancellationToken);
                foreach (var entry in EntriesOf(index))
                {
                    entries[entry.Key] = entry;
                }
            }
            catch (JsonException)
            {
                // An index that cannot be read names nothing, so the store holds nothing: every step runs again and
                // the next result written replaces the index. Costly, never wrong.
                entries.Clear();
            }
        }

        return new StepStore(full, entries);
    }

    /// <summary>The path a step of this key is stored at, whether or not it is there yet.</summary>
    public string PathOf(string key) => Path.Combine(Folder, FileNameOf(key));

    /// <summary>The folder a step whose results are named by its run would write into when it is done by hand: what
    /// its command line names. The store keeps nothing there and reads nothing from it.</summary>
    public string FolderOf(string key) => Path.Combine(Folder, DigestOf(key));

    /// <summary>The stored result of the step with this key, or null when the store does not hold it: the index must
    /// name it, and the file and every file that belongs beside it must be there.</summary>
    public string? Find(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var path = Path.Combine(Folder, entry.File);
        return File.Exists(path) && CompanionsOf(entry).All(companion => File.Exists(Path.Combine(Folder, companion))) ? path : null;
    }

    /// <summary>True when the step with this key is in the index and says it left out its result of this name: a step
    /// that may leave one out, and did. False for a result it gave, and for a step the index does not name.</summary>
    public bool LeftOut(string key, string result)
        => _entries.TryGetValue(key, out var entry) && !entry.LeftOut.IsDefault && entry.LeftOut.Contains(result, StringComparer.Ordinal);

    private static ImmutableArray<string> CompanionsOf(StepStoreEntry entry) => entry.Companions.IsDefault ? [] : entry.Companions;

    /// <summary>A place inside the store for the step with this key to write its result, taken into the store by
    /// <see cref="CommitAsync"/> and removed when disposed.</summary>
    public StepStaging Stage(string key) => Stage(key, ".fits");

    /// <summary>As <see cref="Stage(string)"/>, for a result that is no image: its file ends as
    /// <paramref name="ending"/> says.</summary>
    public StepStaging Stage(string key, string ending)
    {
        var folder = Path.Combine(Folder, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new StepStaging(key, folder, Path.Combine(folder, FileNameOf(key, ending)));
    }

    /// <summary>A folder inside the store for a run to write into whatever its verb writes, removed when disposed.
    /// What the run names of it is taken into the store by <see cref="AdoptAsync"/>.</summary>
    public StepWork Work()
    {
        var folder = Path.Combine(Folder, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new StepWork(folder);
    }

    /// <summary>
    /// Takes a file a run wrote into the store under <paramref name="key"/>, with the files that belong beside it,
    /// and names them all in the index. Each companion keeps its ending and takes the result's name in the store, so
    /// what finds a sidecar beside a master finds it beside the stored one.
    /// </summary>
    /// <returns>The stored file.</returns>
    /// <exception cref="ArgumentException">A companion is not named as the file is without its ending
    /// (<c>master_x.coverage.fits</c> beside <c>master_x.fits</c>), which is how a master's sidecars are named.</exception>
    public async Task<string> AdoptAsync(string key, string file, IReadOnlyList<string> companions, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companions);
        // The file's name without its ending, as a master's sidecars are named after it: a group whose name holds a
        // dot (an exposure of 0.5 s) keeps what belongs beside it.
        var name = Path.GetFileName(file);
        var stem = Path.GetFileNameWithoutExtension(name);
        var digest = DigestOf(key);
        Directory.CreateDirectory(Folder);
        var beside = ImmutableArray.CreateBuilder<string>(companions.Count);
        foreach (var companion in companions)
        {
            var companionName = Path.GetFileName(companion);
            if (!companionName.StartsWith(stem + ".", StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{companionName}' is not named as '{name}' is, so it cannot be found beside it", nameof(companions));
            }

            var stored = digest + companionName[stem.Length..];
            File.Move(companion, Path.Combine(Folder, stored), overwrite: true);
            beside.Add(stored);
        }

        // The result itself last: until it is there, nothing reads what is beside it.
        var storedName = digest + name[stem.Length..];
        var storedFile = Path.Combine(Folder, storedName);
        File.Move(file, storedFile, overwrite: true);
        var entry = new StepStoreEntry { Key = key, Operation = operationId, File = storedName, Bytes = new FileInfo(storedFile).Length, Companions = beside.ToImmutable() };
        await SharedFile.UpdateJsonAsync(
            Path.Combine(Folder, IndexFileName),
            WorkbenchJsonContext.Default.StepStoreIndex,
            current => With(current, entry),
            logger: null,
            cancellationToken);
        _entries[entry.Key] = entry;
        return storedFile;
    }

    /// <summary>Takes a staged result into the store and names it in the index.</summary>
    /// <returns>The stored file.</returns>
    public Task<string> CommitAsync(StepStaging staging, string operationId, CancellationToken cancellationToken = default)
        => CommitAsync(staging, operationId, [], cancellationToken);

    /// <summary>Takes a step's staged main result into the store and names it in the index, with the results the step
    /// may leave out and did (<see cref="StepStoreEntry.LeftOut"/>).</summary>
    /// <returns>The stored file.</returns>
    public async Task<string> CommitAsync(StepStaging staging, string operationId, ImmutableArray<string> leftOut, CancellationToken cancellationToken = default)
    {
        // Named as it was staged: an image, or a result that is none (a list of a step's results).
        var file = Path.GetFileName(staging.Path);
        var stored = Path.Combine(Folder, file);
        File.Move(staging.Path, stored, overwrite: true);

        var entry = new StepStoreEntry
        {
            Key = staging.Key, Operation = operationId, File = file, Bytes = new FileInfo(stored).Length, LeftOut = leftOut.IsDefault ? [] : leftOut,
        };
        await SharedFile.UpdateJsonAsync(
            Path.Combine(Folder, IndexFileName),
            WorkbenchJsonContext.Default.StepStoreIndex,
            current => With(current, entry),
            logger: null,
            cancellationToken);
        _entries[entry.Key] = entry;
        return stored;
    }

    // An index's entries; none for no index, and for one whose list was written as null.
    private static ImmutableArray<StepStoreEntry> EntriesOf(StepStoreIndex? index)
        => index is null || index.Entries.IsDefault ? [] : index.Entries;

    // The index with this entry in it, in place of an earlier one of the same key.
    private static StepStoreIndex With(StepStoreIndex? current, StepStoreEntry entry)
    {
        var entries = ImmutableArray.CreateBuilder<StepStoreEntry>();
        foreach (var existing in EntriesOf(current))
        {
            if (!string.Equals(existing.Key, entry.Key, StringComparison.Ordinal))
            {
                entries.Add(existing);
            }
        }

        entries.Add(entry);
        return new StepStoreIndex { Entries = entries.ToImmutable() };
    }
}

/// <summary>A folder a run writes into on its way into the store (<see cref="StepStore.Work"/>).</summary>
public sealed class StepWork : IDisposable
{
    internal StepWork(string folder) => Folder = folder;

    /// <summary>The folder the operation writes into.</summary>
    public string Folder { get; }

    /// <summary>Removes the folder, with whatever the run wrote that the store did not take.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program holds something in it. What is left is a folder the store's index does not name, so
            // it is never read as a result; a run that failed for another reason must not fail again on tidying up.
        }
    }
}

/// <summary>
/// The list a step whose results are named by its run keeps under its own key: the names, in the order the run gave
/// them. With it a status knows what such a step gave without running it, and whether all of it is still there.
/// </summary>
public static class StepResultsList
{
    /// <summary>How the list's file ends in the store.</summary>
    public const string Ending = ".results.json";

    /// <summary>Writes the list to <paramref name="path"/>.</summary>
    public static async Task WriteAsync(string path, ImmutableArray<string> names, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new StepResultsFile { Results = names }, WorkbenchJsonContext.Default.StepResultsFile, cancellationToken);
    }

    /// <summary>The names in the list at <paramref name="path"/>; false when it is not there or cannot be read, which
    /// is a step that has to run again.</summary>
    public static bool TryRead(string path, out ImmutableArray<string> names)
    {
        names = [];
        try
        {
            if (JsonSerializer.Deserialize(File.ReadAllBytes(path), WorkbenchJsonContext.Default.StepResultsFile) is not { } read)
            {
                return false;
            }

            names = read.Results.IsDefault ? [] : read.Results;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>A step's result on its way into the store (<see cref="StepStore.Stage"/>).</summary>
public sealed class StepStaging : IDisposable
{
    private readonly string _folder;

    internal StepStaging(string key, string folder, string path)
    {
        Key = key;
        _folder = folder;
        Path = path;
    }

    /// <summary>The step's key.</summary>
    public string Key { get; }

    /// <summary>The file the operation writes.</summary>
    public string Path { get; }

    /// <summary>Removes the staging folder, with a result that was not taken into the store.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program holds something in it. What is left is a folder the store's index does not name, so
            // it is never read as a result; a run that failed for another reason must not fail again on tidying up.
        }
    }
}
