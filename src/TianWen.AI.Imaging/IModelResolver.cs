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
}
