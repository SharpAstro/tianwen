using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// A project: a folder holding <c>project.json</c> (this) beside the step store (docs/plans/stacking-workbench.md,
/// "The project"). It names the original files and, for each, the ordered list of edits made to it. No original is
/// ever written; every result goes to the store.
/// </summary>
/// <remarks>
/// Every property with a default is settable, never init-only: the JSON source generator gives an absent init-only
/// property its type's default, not the one declared here.
/// </remarks>
public sealed record WorkbenchProject
{
    /// <summary>The layout this code writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The layout the file was written in.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The images, each an original file and its edit list.</summary>
    public ImmutableArray<ProjectImage> Images { get; set; } = [];
}

/// <summary>One image of a project: an original file and the edits made to it, in order.</summary>
public sealed record ProjectImage
{
    /// <summary>What the image is called in this project. Another image's edit names it to take its result as an
    /// input (a linear fit's reference).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The original file, absolute or relative to the project's folder. Never written. Empty for an image
    /// that starts from another (<see cref="From"/>).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>In place of a file: the image of this project it starts from, as that image's own edits leave it. So
    /// one image can go on from where another stops: the masters of a colour image before and after the run they all
    /// go through together.</summary>
    public string? From { get; set; }

    /// <summary>With <see cref="From"/>: start from this extra result of that image in place of its main one
    /// (<see cref="OperationDescription.ExtraResults"/>): the stars its star removal took out. It is the result of
    /// the last of that image's edits that gives one of this name.</summary>
    public string? Result { get; set; }

    /// <summary>The digest of the file when a run last read it, so a listing can say the file changed since. The run
    /// itself never trusts it: it digests the file again.</summary>
    public string? Digest { get; set; }

    /// <summary>The edits, first to last.</summary>
    public ImmutableArray<ProjectEdit> Edits { get; set; } = [];
}

/// <summary>One edit: an operation and its settings, applied to what the edits before it made.</summary>
public sealed record ProjectEdit
{
    /// <summary>The operation's id (<see cref="OperationDescription.Id"/>).</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Its settings; a setting left out is at its default, and none at all is every default.</summary>
    public JsonObject? Settings { get; set; }

    /// <summary>The edit's switch. An edit switched off is passed over: the edit after it takes what the edit before
    /// it made. Switching it back on finds its earlier result in the store and runs nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The other images of the project this edit takes, by name, one for each of the operation's extra
    /// inputs: each is that image as its own edits leave it.</summary>
    public ImmutableArray<string> Inputs { get; set; } = [];

    /// <summary>The step's key when a run last made or found its result. Written by a run, for a listing to say what
    /// changed since; never read to decide whether to run.</summary>
    public string? Key { get; set; }

    /// <summary>The result of that run, relative to the project's folder; absent for a derived step, which has no file.</summary>
    public string? Output { get; set; }
}

/// <summary>A project read from its folder, or why it could not be.</summary>
/// <param name="Project">The project, null when it could not be read.</param>
/// <param name="Refusal">Why not, in words; null when it was read.</param>
public readonly record struct ProjectRead(WorkbenchProject? Project, string? Refusal);

/// <summary>Reads and writes a project's <c>project.json</c>.</summary>
public static class WorkbenchProjectFile
{
    /// <summary>The project file's name inside its folder.</summary>
    public const string FileName = "project.json";

    /// <summary>The project file of <paramref name="folder"/>.</summary>
    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>Reads the project in <paramref name="folder"/>.</summary>
    public static async Task<ProjectRead> ReadAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = PathIn(folder);
        await using var stream = await SharedFile.TryOpenReadAsync(path, cancellationToken);
        if (stream is null)
        {
            return new ProjectRead(null, $"there is no {FileName} in {folder}");
        }

        WorkbenchProject? project;
        try
        {
            project = await JsonSerializer.DeserializeAsync(stream, WorkbenchJsonContext.Default.WorkbenchProject, cancellationToken);
        }
        catch (JsonException ex)
        {
            return new ProjectRead(null, $"{path} cannot be read: {ex.Message}");
        }

        if (project is null)
        {
            return new ProjectRead(null, $"{path} holds no project");
        }

        return project.SchemaVersion > WorkbenchProject.CurrentSchemaVersion
            ? new ProjectRead(null, $"{path} was written by a newer TianWen (layout {project.SchemaVersion}, this one reads up to {WorkbenchProject.CurrentSchemaVersion})")
            : new ProjectRead(project, null);
    }

    /// <summary>Writes the project into <paramref name="folder"/>, all at once: a reader sees the old file or the new.</summary>
    public static Task WriteAsync(string folder, WorkbenchProject project, CancellationToken cancellationToken = default)
        => SharedFile.WriteAsync(
            PathIn(folder),
            (stream, token) => JsonSerializer.SerializeAsync(stream, project, WorkbenchJsonContext.Default.WorkbenchProject, token),
            cancellationToken);
}

/// <summary>The step store's own list of what it wrote (<see cref="StepStore"/>).</summary>
public sealed record StepStoreIndex
{
    /// <summary>The layout the file was written in.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>One entry for each result the store holds.</summary>
    public ImmutableArray<StepStoreEntry> Entries { get; set; } = [];
}

/// <summary>One result in the step store.</summary>
public sealed record StepStoreEntry
{
    /// <summary>The step's key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The operation that made it, for a person reading the index.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>The file's name inside the store's folder.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>The file's size when it was written.</summary>
    public long Bytes { get; set; }
}

// Files a person reads and may write by hand: indented, camelCase, enums as words. A member the type does not have is
// refused, not passed over: an "edit" for "edits" would otherwise be an image with no edits, and say nothing.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkbenchProject))]
[JsonSerializable(typeof(StepStoreIndex))]
public sealed partial class WorkbenchJsonContext : JsonSerializerContext
{
}
