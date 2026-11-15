using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.IO;

/// <summary>
/// Data files a host serves from a folder on the web, beside the app (#2161): what a build switched off in the
/// assembly (<c>TianWen.Lib.EmbeddedData.&lt;group&gt;</c>) and published as plain files instead, so a new build
/// of the code does not make a visitor download the catalogues again.
/// </summary>
/// <remarks>
/// <para>The folder says what it carries: <see cref="IndexFileName"/> lists one file name a line, and a file not
/// on it is answered null at once, without a request (a reader asks for files only some builds have). A folder
/// with no index carries nothing, which is a dev server or a build that embeds everything, and not a fault.</para>
/// <para>Everything on the index is fetched as soon as the index is read, all at once, and kept: the catalogue
/// database asks for most of it in turn, some of it twice, and one request after another would cost a round
/// trip each. A file that fails to arrive fails the reader that asks for it, naming the file.</para>
/// </remarks>
public sealed class HttpDataFiles : IDataFileSource
{
    /// <summary>The list of the files a folder carries, one name a line.</summary>
    public const string IndexFileName = "index.txt";

    // Slash, backslash, colon, question mark, hash: what would turn a name into a path or an address of its own.
    private static readonly char[] NotInAName = [(char)47, (char)92, (char)58, (char)63, (char)35];

    private readonly HttpClient _http;
    private readonly Uri _folder;
    private readonly ConcurrentDictionary<string, Task<byte[]>> _fetches = new(StringComparer.Ordinal);
    private Task<FrozenSet<string>> _index;

    /// <param name="http">The client the files are fetched with; its lifetime is the caller's.</param>
    /// <param name="folder">The folder's address. It is taken as a folder whether or not it ends in a slash.</param>
    public HttpDataFiles(HttpClient http, Uri folder)
    {
        _http = http;
        _folder = folder.AbsoluteUri.EndsWith('/') ? folder : new Uri(folder.AbsoluteUri + "/");
        // Started here and never under a caller's token: the fetches are this source's, shared by every reader.
        _index = ReadIndexAsync();
    }

    public async ValueTask<Stream?> OpenAsync(string fileName, CancellationToken cancellationToken = default)
    {
        // A fetch that failed is not kept: the reader that asked was told, and the next one asks the web again,
        // so a dropped request does not leave a catalogue gone until the page is loaded anew.
        var index = Volatile.Read(ref _index);
        if (index.IsFaulted)
        {
            var again = ReadIndexAsync();
            index = Interlocked.CompareExchange(ref _index, again, index) == index ? again : Volatile.Read(ref _index);
        }

        if (!(await index.WaitAsync(cancellationToken)).Contains(fileName))
        {
            return null;
        }

        var fetch = _fetches.GetOrAdd(fileName, FetchAsync);
        if (fetch.IsFaulted)
        {
            var again = FetchAsync(fileName);
            fetch = _fetches.TryUpdate(fileName, again, fetch) ? again : _fetches.GetOrAdd(fileName, again);
        }

        return new MemoryStream(await fetch.WaitAsync(cancellationToken), writable: false);
    }

    public Stream? OpenIfAtHand(string fileName)
        => _fetches.TryGetValue(fileName, out var fetch) && fetch is { IsCompletedSuccessfully: true, Result: var bytes }
            ? new MemoryStream(bytes, writable: false)
            : null;

    private async Task<FrozenSet<string>> ReadIndexAsync()
    {
        using var response = await _http.GetAsync(new Uri(_folder, IndexFileName), HttpCompletionOption.ResponseContentRead);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return FrozenSet<string>.Empty;
        }

        response.EnsureSuccessStatusCode();
        var names = (await response.Content.ReadAsStringAsync())
            .Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // A name, never a path: the index is data, and a line with a separator in it would leave the folder.
            .Where(name => name.IndexOfAny(NotInAName) < 0)
            .ToFrozenSet(StringComparer.Ordinal);

        // All of it at once, while the first reader is still on its way here.
        foreach (var name in names)
        {
            _fetches.GetOrAdd(name, FetchAsync);
        }

        return names;
    }

    private async Task<byte[]> FetchAsync(string fileName)
    {
        try
        {
            return await _http.GetByteArrayAsync(new Uri(_folder, fileName));
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"The data file {fileName} is listed in {new Uri(_folder, IndexFileName)} but could not be fetched: {ex.Message}", ex);
        }
    }
}
