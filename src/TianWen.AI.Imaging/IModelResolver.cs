namespace TianWen.AI.Imaging;

/// <summary>
/// Resolves AI enhancement model filenames (e.g. <c>darkstar_color_AI4.onnx</c>)
/// to absolute paths on disk. The default <see cref="ModelResolver"/> looks
/// under <c>%LOCALAPPDATA%/TianWen/models</c> (the path written by
/// <c>tools/tianwen-ai-models-fetch.ps1</c>) with an optional fallback to the
/// SetiAstroSuite Pro install at <c>%LOCALAPPDATA%/SASpro/models</c> so a
/// dual-app developer install can share weights without a re-fetch.
/// </summary>
public interface IModelResolver
{
    /// <summary>
    /// Returns the absolute path to <paramref name="modelFileName"/> from
    /// the first configured search location that contains it. The file name
    /// is the bare model name including the extension (e.g.
    /// <c>"darkstar_color_AI4.onnx"</c>) -- no directory components.
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException">
    /// No configured search location contains the file. The exception
    /// message lists every path that was probed so the user can run the
    /// fetch script (<c>tools/tianwen-ai-models-fetch.ps1</c>).
    /// </exception>
    string Resolve(string modelFileName);

    /// <summary>
    /// Non-throwing variant of <see cref="Resolve"/>. Returns <c>true</c> and
    /// sets <paramref name="absolutePath"/> when the file is found, otherwise
    /// returns <c>false</c> and sets it to <c>null</c>.
    /// </summary>
    bool TryResolve(string modelFileName, out string? absolutePath);

    /// <summary>Directories this resolver searches, in priority order. Empty when a resolver has no
    /// directory notion (a test fake mapping names straight to paths).</summary>
    System.Collections.Immutable.ImmutableArray<string> SearchPaths
        => System.Collections.Immutable.ImmutableArray<string>.Empty;

    /// <summary>
    /// Diagnostic form of <see cref="TryResolve"/>: says WHICH of the search directories answered,
    /// how big the file is, and every path that was tried when nothing did.
    /// <para>
    /// Default implementation is deliberately two-state (present / absent), built on
    /// <see cref="TryResolve"/> so an existing implementer keeps compiling.
    /// <see cref="ModelResolver"/> overrides it to also report
    /// <see cref="ModelPresenceKind.PointerStub"/>, which is the state that looks like success to
    /// <c>File.Exists</c> and like absence to <see cref="TryResolve"/>.
    /// </para>
    /// </summary>
    ModelPresence Probe(string modelFileName)
        => TryResolve(modelFileName, out var path) && path is not null
            ? new ModelPresence(modelFileName, ModelPresenceKind.Present, path, 0,
                System.Collections.Immutable.ImmutableArray<string>.Empty)
            : new ModelPresence(modelFileName, ModelPresenceKind.Absent, null, 0,
                System.Collections.Immutable.ImmutableArray<string>.Empty);
}

/// <summary>Whether a model's weights are actually usable. See <see cref="IModelResolver.Probe"/>
/// for why the middle case needs a name of its own.</summary>
public enum ModelPresenceKind
{
    /// <summary>No search directory holds the file.</summary>
    Absent,

    /// <summary>The file is there but is a ~130-byte Git LFS pointer, not weights. Remedy is
    /// <c>git lfs pull</c>, not the fetch script.</summary>
    PointerStub,

    /// <summary>Real weights, at <see cref="ModelPresence.Path"/>.</summary>
    Present,
}

/// <param name="FileName">Bare model file name that was probed.</param>
/// <param name="Kind">Whether the weights are usable.</param>
/// <param name="Path">Absolute path that answered, or <c>null</c> when <see cref="ModelPresenceKind.Absent"/>.</param>
/// <param name="Bytes">Size of the resolved file; 0 when absent, a stub, or unreadable.</param>
/// <param name="ProbedPaths">Every candidate tried, in order -- the list a user needs to see when
/// a model is missing, so they know where to put it.</param>
public readonly record struct ModelPresence(
    string FileName,
    ModelPresenceKind Kind,
    string? Path,
    long Bytes,
    System.Collections.Immutable.ImmutableArray<string> ProbedPaths);
