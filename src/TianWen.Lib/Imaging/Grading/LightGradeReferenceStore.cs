using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Grading;

/// <summary>
/// The clear nights of each rig, kept across folders and runs of the viewer (#1976) so a run that is cloud from first frame to last
/// is judged against a clear night of its rig and not against itself (<see cref="LightGrading.Verdict"/>). One file under AppData,
/// <c>Viewer/clear-sky.json</c>, which every viewer process adds to, so it is written through <c>IExternal.UpdateJsonAsync</c>
/// (two viewers cannot lose each other's nights) and read through <c>TryReadJsonAsync</c>. What counts as clear enough to record is
/// <see cref="LightGrading.ClearNights"/>'s: a night with at least <see cref="LightGrading.MinClearFramesPerReference"/> frames
/// whose verdict carries no cloud and no twilight cause, none of them judged against another night's reference; a cloudy run is
/// never recorded. Each rig and site keeps its <see cref="LightGrading.MaxReferenceNightsPerRig"/> most recent nights.
/// </summary>
public static class LightGradeReferenceStore
{
    /// <summary>Where the references are kept: <c>Viewer/clear-sky.json</c>.</summary>
    public static string PathFor(IExternal external) => Path.Combine(external.AppDataFolder.FullName, "Viewer", "clear-sky.json");

    /// <summary>Every kept reference; empty (never default) when none was saved, so a verdict read through it says cloud was judged on that night alone.</summary>
    public static async Task<ImmutableArray<LightGradeReference>> LoadAsync(IExternal external, CancellationToken cancellationToken)
        => await external.TryReadJsonAsync(PathFor(external), LightGradeReferenceJsonContext.Default.LightGradeReferenceFile, logger: null, cancellationToken)
            .ConfigureAwait(false) is { } saved
            ? [.. saved.References]
            : [];

    /// <summary>
    /// Adds the clear nights of <paramref name="grades"/> (read through <paramref name="loaded"/>, the references the verdicts were
    /// shown with) to the file, and returns what the file holds afterwards, or <paramref name="loaded"/> when no night qualified and
    /// nothing was written.
    /// </summary>
    public static async Task<ImmutableArray<LightGradeReference>> RecordAsync(IExternal external, ImmutableDictionary<string, LightGrade> grades,
        ImmutableArray<LightGradeReference> loaded, CancellationToken cancellationToken)
    {
        var nights = LightGrading.ClearNights(grades, loaded);
        if (nights.IsEmpty)
        {
            return loaded;
        }
        var path = PathFor(external);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? external.AppDataFolder.FullName);
        var after = ImmutableArray<LightGradeReference>.Empty;
        await external.UpdateJsonAsync(path, LightGradeReferenceJsonContext.Default.LightGradeReferenceFile, saved =>
        {
            after = LightGrading.MergeReferences(saved?.References ?? [], nights);
            return new LightGradeReferenceFile([.. after]);
        }, logger: null, cancellationToken).ConfigureAwait(false);
        return after;
    }
}

/// <summary>The file: a plain list, ending on a non-nullable value (<c>JsonStreamReadTrapTests</c>).</summary>
public sealed record LightGradeReferenceFile(List<LightGradeReference> References, int Version = 1);

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(LightGradeReferenceFile))]
internal partial class LightGradeReferenceJsonContext : JsonSerializerContext;
