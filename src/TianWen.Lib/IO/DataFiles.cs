using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.IO;

/// <summary>
/// Where the library's data files come from: the catalogues, the star cross-references, the filter and sensor
/// curves, a simulated device's pictures. By default they are embedded in this assembly, but a trimmed consumer
/// may switch a group of them off (<c>TianWen.Lib.EmbeddedData.&lt;group&gt;</c>, <c>ILLink.Substitutions.xml</c>)
/// and then they are not there to read. A host that still wants such a file supplies it itself, from a disk or
/// over the web, by putting its own source behind the embedded one (<see cref="DataFiles.Source"/>, #2161).
/// </summary>
/// <remarks>
/// <para>A file is asked for by its NAME (<c>NGC.gs.gz</c>, <c>tyc2_gsc_bounds.bin.lz</c>), never by a path or a
/// manifest resource name. A source is asked from several threads at once and must answer each on its own
/// stream, which the caller disposes. The stream is read once from where it stands, start to end: it need not
/// seek nor know its length, so a response body will do.</para>
/// <para>The same file is asked for more than once (a snapshot's hash reads the catalogues its reader reads
/// again, and a reader may probe for a file before opening it), so a source that fetches keeps what it fetched.
/// A source should also answer null at once for a file it knows it does not carry: a reader asks for files that
/// only some builds have (<c>tyc2.bin.lz</c>, an expanded <c>.bin</c> beside its <c>.lz</c>).</para>
/// </remarks>
public interface IDataFileSource
{
    /// <summary>
    /// Opens a file, waiting for it where it has to be fetched. Null when this source does not have the file,
    /// which is an answer and not a fault: the reader then does without it, or says which file is missing.
    /// </summary>
    /// <exception cref="IOException">
    /// The source has the file and could not get it (a request that failed or timed out). A reader of a file the
    /// catalogue can do without catches this and does without; for a file it needs, the fault is the caller's to see.
    /// </exception>
    ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file only where that needs no waiting: an embedded resource, or bytes the source already holds.
    /// Null where the file would have to be fetched first as well as where the source does not have it. For a
    /// reader that cannot wait (one called from a drawing loop); such a reader's host awaits
    /// <see cref="OpenAsync"/> through the reader's own load step beforehand.
    /// </summary>
    Stream? OpenIfAtHand(string fileName);
}

/// <summary>The process's source of data files, and the sources a host builds its own from.</summary>
public static class DataFiles
{
    // Null until a host sets one: the embedded files then answer (see Source).
    private static IDataFileSource? _source;

    /// <summary>The files embedded in TianWen.Lib, less whatever groups the consumer trimmed away.</summary>
    public static IDataFileSource Embedded { get; } = new EmbeddedDataFiles(typeof(DataFiles).Assembly);

    /// <summary>
    /// The source every reader in the library asks. ONE decision per process: a host that supplies files sets
    /// it once at startup, before anything reads (<c>DataFiles.Source = DataFiles.FirstOf(DataFiles.Embedded,
    /// mine)</c>). What a reader already read from the previous source stays read.
    /// </summary>
    public static IDataFileSource Source
    {
        get => Volatile.Read(ref _source) ?? Embedded;
        set => Volatile.Write(ref _source, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>A source that asks each of <paramref name="sources"/> in order and answers with the first that has the file.</summary>
    public static IDataFileSource FirstOf(params IDataFileSource[] sources) => new FirstOfDataFiles(sources);

    private sealed class EmbeddedDataFiles(Assembly assembly) : IDataFileSource
    {
        // A manifest resource is named <root namespace>.<folders>.<file>, so a file is found by its dotted suffix.
        // Resolved once a name: the manifest is a few dozen entries, and a reader asks for the same files again.
        private readonly ConcurrentDictionary<string, string?> _manifestNames = new(StringComparer.Ordinal);
        private string[]? _all;

        public ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
            => cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled<Stream?>(cancellationToken)
                : new ValueTask<Stream?>(OpenIfAtHand(fileName));

        public Stream? OpenIfAtHand(string fileName)
        {
            var manifest = _manifestNames.GetOrAdd(fileName, static (name, self) =>
            {
                var all = self._all ??= self.Assembly.GetManifestResourceNames();
                var suffix = "." + name;
                return Array.Find(all, n => n.EndsWith(suffix, StringComparison.Ordinal));
            }, this);
            return manifest is null ? null : assembly.GetManifestResourceStream(manifest);
        }

        private Assembly Assembly => assembly;
    }

    private sealed class FirstOfDataFiles(IDataFileSource[] sources) : IDataFileSource
    {
        public async ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
        {
            foreach (var source in sources)
            {
                if (await source.OpenAsync(fileName, cancellationToken) is { } stream)
                {
                    return stream;
                }
            }

            return null;
        }

        public Stream? OpenIfAtHand(string fileName)
        {
            foreach (var source in sources)
            {
                if (source.OpenIfAtHand(fileName) is { } stream)
                {
                    return stream;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Files a host holds in memory: fetched ahead of time, or read off a disk. Each answer is a read-only stream
/// over the same bytes, so the bytes must not be changed once added.
/// </summary>
public sealed class InMemoryDataFiles : IDataFileSource
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public InMemoryDataFiles()
    {
    }

    public InMemoryDataFiles(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        foreach (var (name, bytes) in files)
        {
            Add(name, bytes);
        }
    }

    /// <summary>Adds or replaces a file. Readers that already opened the previous bytes keep them.</summary>
    public void Add(string fileName, byte[] bytes) => _files[fileName] = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
        => cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<Stream?>(cancellationToken)
            : new ValueTask<Stream?>(OpenIfAtHand(fileName));

    public Stream? OpenIfAtHand(string fileName)
        => _files.TryGetValue(fileName, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
}
