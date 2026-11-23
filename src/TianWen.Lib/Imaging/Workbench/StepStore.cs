using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
    public static string FileNameOf(string key) => key[(key.IndexOf(':') + 1)..] + ".fits";

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

    /// <summary>The stored result of the step with this key, or null when the store does not hold it: the index must
    /// name it and the file must be there.</summary>
    public string? Find(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var path = Path.Combine(Folder, entry.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>A place inside the store for the step with this key to write its result, taken into the store by
    /// <see cref="CommitAsync"/> and removed when disposed.</summary>
    public StepStaging Stage(string key)
    {
        var folder = Path.Combine(Folder, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new StepStaging(key, folder, Path.Combine(folder, FileNameOf(key)));
    }

    /// <summary>Takes a staged result into the store and names it in the index.</summary>
    /// <returns>The stored file.</returns>
    public async Task<string> CommitAsync(StepStaging staging, string operationId, CancellationToken cancellationToken = default)
    {
        var file = FileNameOf(staging.Key);
        var stored = Path.Combine(Folder, file);
        File.Move(staging.Path, stored, overwrite: true);

        var entry = new StepStoreEntry { Key = staging.Key, Operation = operationId, File = file, Bytes = new FileInfo(stored).Length };
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
