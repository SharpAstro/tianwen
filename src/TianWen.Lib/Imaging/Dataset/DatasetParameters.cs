using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// The dataset's standing DECISIONS, kept in a file rather than in whoever launched the last bake.
/// </summary>
/// <remarks>
/// <para><b>A command-line flag that must be repeated forever is a decision that decays.</b> Excluding
/// a session from training is a judgement about the DATA, true for every future bake, and expressing
/// it as an argument means it survives only as long as someone remembers to retype it: forget it once
/// and the session is silently back in training, with nothing to notice.</para>
/// <para>It also carries the thing an argument cannot, which is WHY. A bare session id in a shell
/// history is unreviewable six months later; "the field rotation leaves the canvas mostly partial
/// coverage, keep it as an auto-crop fixture" is a decision someone can agree or disagree with.</para>
/// <para>Deliberately NOT a full copy of the command line. Per-run things (roots, scratch, output)
/// stay arguments, because they genuinely differ per run and are already captured in
/// <c>bake-provenance.json</c>. What lives here is only what should outlive the run.</para>
/// </remarks>
/// <param name="HeldOutSessions">Sessions forced into the held-out TEST split, each with its reason.</param>
/// <param name="ExcludePathSegments">Extra wildcard path segments to exclude, appended to the
/// built-in processed-data exclusions. Null leaves the defaults alone.</param>
/// <param name="TestFraction">Held-out fraction, when the file should pin it. Null keeps the default.</param>
public sealed record DatasetParameters(
    ImmutableArray<HeldOutSession> HeldOutSessions = default,
    ImmutableArray<string> ExcludePathSegments = default,
    double? TestFraction = null)
{
    /// <summary>Conventional file name.</summary>
    public const string FileName = "dataset-parameters.json";

    /// <summary>
    /// Reads the file, or returns an empty set when it does not exist.
    /// </summary>
    /// <remarks>
    /// A MISSING file is not an error, so a fresh checkout bakes with defaults. A file that exists
    /// but does not parse IS an error and is left to throw: silently baking without the exclusions
    /// someone wrote down is the failure this type exists to prevent.
    /// </remarks>
    public static async Task<DatasetParameters> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new DatasetParameters();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, DatasetParametersJsonContext.Default.DatasetParameters, cancellationToken)
               ?? new DatasetParameters();
    }

    /// <summary>The held-out session ids alone.</summary>
    public ImmutableArray<string> HeldOutSessionIds
        => HeldOutSessions.IsDefaultOrEmpty ? [] : [.. System.Linq.Enumerable.Select(HeldOutSessions, h => h.Session)];
}

/// <summary>One session kept out of training, and why.</summary>
/// <param name="Session">The session id, exactly as it appears in <c>stats/psf-sessions.jsonl</c>.</param>
/// <param name="Reason">Why it must never train. Required in spirit: an entry with no reason is a
/// decision nobody can review later.</param>
public sealed record HeldOutSession(string Session, string Reason = "");

[JsonSerializable(typeof(DatasetParameters))]
[JsonSerializable(typeof(HeldOutSession))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class DatasetParametersJsonContext : JsonSerializerContext;
