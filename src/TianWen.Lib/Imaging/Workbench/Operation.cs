using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// One unit of work on an image: typed settings, one routine, one command-line verb
/// (docs/plans/stacking-workbench.md, "An operation"). This is the face the edit list and the runner use, where
/// settings are JSON; the typed face is <see cref="Operation{TSettings}"/>, which a verb calls.
/// </summary>
/// <remarks>
/// Settings cross this interface in their canonical form: the JSON <see cref="TryReadSettings"/> answers, every setting
/// stated, in the type's own order. A step's key is made from that text, so two ways of writing the same settings (a
/// default left out, or stated) are one step.
/// </remarks>
public interface IOperation
{
    /// <summary>What the operation is.</summary>
    OperationDescription Description { get; }

    /// <summary>Its settings at their defaults, in canonical form.</summary>
    string DefaultSettings { get; }

    /// <summary>The settings type's source-generated JSON contract (<see cref="OperationSettingsJsonContext"/>), which a panel derives its controls from (<see cref="OperationSettingsForm"/>); null for an operation that offers none.</summary>
    JsonTypeInfo? SettingsContract => null;

    /// <summary>
    /// Reads an edit's settings (null or empty for the defaults) into canonical form; false with the reason in words for
    /// JSON that is not this operation's settings, a setting it does not have, or values it refuses.
    /// </summary>
    bool TryReadSettings(string? settingsJson, [NotNullWhen(true)] out string? canonical, [NotNullWhen(false)] out string? refusal);

    /// <summary>
    /// What would do the work for these settings on this computer, beyond the operation's own code: the backend and model
    /// of a step a model serves (the plan's rule 7). Part of the step's key, so a result one backend made is never taken
    /// for another's. Empty for an operation that is its own code only.
    /// </summary>
    string ServedBy(string canonicalSettings);

    /// <summary>
    /// One short word for whose models <see cref="ServedBy"/> names, for a line a person reads: <c>rc</c> (RC-Astro),
    /// <c>tw</c> (TianWen's own) or <c>mix</c> (<see cref="Enhancement.EnhancerOrigin.TagOf"/>). Empty for an operation no
    /// model serves, and where what serves it does not say whose it is. It is no part of the step's key.
    /// </summary>
    string ServedTag(string canonicalSettings) => string.Empty;

    /// <summary>
    /// <see cref="ServedBy"/> and <see cref="ServedTag"/> from one asking: naming what serves can cost a licence probe,
    /// so a caller that wants both asks this, and an operation that pays for the answer pays once.
    /// </summary>
    (string By, string Tag) Served(string canonicalSettings) => (ServedBy(canonicalSettings), ServedTag(canonicalSettings));

    /// <summary>
    /// The command that does this step from a terminal, program name first (the plan's rule 3): the verb, the inputs in
    /// the order the edit list hands them, the settings and the output.
    /// </summary>
    ImmutableArray<string> CommandLine(string canonicalSettings, IReadOnlyList<string> inputs, string output);

    /// <summary>
    /// Does the work and writes <paramref name="outputPath"/>: the verb's routine. A refusal writes nothing.
    /// </summary>
    /// <param name="canonicalSettings">The settings, as <see cref="TryReadSettings"/> answered them.</param>
    /// <param name="inputs">The edit list's image first, then the description's extra inputs in order.</param>
    /// <param name="outputPath">The FITS file to write.</param>
    /// <param name="say">Takes each line the operation reports (what a verb prints), or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    ValueTask<OperationOutcome> RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="RunAsync(string, IReadOnlyList{OperationInput}, string, Action{string}?, CancellationToken)"/>, with a place to
    /// report how far the work is, as a fraction in [0, 1]. The runner calls this one. An operation that reports no fraction
    /// keeps this default, which is the routine above; one with engine progress overrides it and the routine above is
    /// then this one with no <paramref name="progress"/>, so a verb and the runner reach the same code.
    /// </summary>
    ValueTask<OperationOutcome> RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunAsync(canonicalSettings, inputs, outputPath, say, cancellationToken);
}

/// <summary>
/// An operation whose result is made again from its input whenever it is asked for and never stored
/// (<see cref="OperationOutputKind.Derived"/>). <see cref="IOperation.RunAsync"/> is still its verb's routine: what
/// writes the result as a file when a person asks for one.
/// </summary>
public interface IDerivedOperation : IOperation
{
    /// <summary>The result as an input for the step after it: nothing is read or computed until that step reads it.</summary>
    OperationInput Derive(string canonicalSettings, IReadOnlyList<OperationInput> inputs);
}

/// <summary>
/// An operation that writes no pixels (<see cref="OperationOutputKind.Finding"/>): it finds something out about its
/// input, a plate solution, and its result is its input with that applied. The finding is a small file in the step
/// store, so the step is not run again, and the pixels stay where they were.
/// <see cref="IOperation.RunAsync"/> is still its verb's routine: it finds, and writes the result as a file.
/// </summary>
public interface IFindingOperation : IOperation
{
    /// <summary>Finds it out and writes the finding to <paramref name="findingPath"/>. A refusal writes nothing.</summary>
    /// <param name="canonicalSettings">The settings, as <see cref="IOperation.TryReadSettings"/> answered them.</param>
    /// <param name="inputs">The edit list's image first, then the description's extra inputs in order.</param>
    /// <param name="findingPath">The file the finding is written to.</param>
    /// <param name="say">Takes each line the operation reports (what a verb prints), or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    ValueTask<OperationOutcome> FindAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>The result as an input for the step after it: the step's input with the finding in
    /// <paramref name="findingPath"/> applied. Nothing is read until that step reads it.</summary>
    OperationInput Apply(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath);

    /// <summary>Writes the result as the file the verb writes, from a finding made before: nothing is found out a
    /// second time.</summary>
    OperationOutcome Write(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath, string outputPath);
}

/// <summary>Where one image's results of a run go.</summary>
/// <param name="Path">The FITS file its main result is written to.</param>
public sealed record StepOutputs(string Path)
{
    /// <summary>The file each extra result is written to, by the result's name (<see cref="OperationDescription.ExtraResults"/>).
    /// A result with no file here is not written.</summary>
    public ImmutableDictionary<string, string> Extra { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// An operation several images go through in one run (<see cref="OperationDescription.Joint"/>): they are put on one
/// scale, so a scale measured between two of them still holds between their results, and each gets its own result.
/// </summary>
/// <remarks>
/// <para><b>The result for an image must not depend on the order the images are handed in.</b> Each image's edit list
/// names the others, and the runner hands them over sorted by key, so every list of one run asks for the same run
/// and it happens once. One image alone is a run of one.</para>
/// </remarks>
public interface IJointOperation : IOperation
{
    /// <summary>The command that does the run from a terminal, program name first: every image and every file it writes.</summary>
    ImmutableArray<string> CommandLine(string canonicalSettings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs);

    /// <summary>Puts <paramref name="members"/> through together and writes each one's results to the files
    /// <paramref name="outputs"/> names for it, in the same order. A refusal writes nothing that counts.</summary>
    ValueTask<OperationOutcome> RunJointAsync(string canonicalSettings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs, Action<string>? say,
        CancellationToken cancellationToken);
}

/// <summary>
/// An operation with fixed inputs, or one that takes a set of images (<see cref="OperationDescription.AnyNumber"/>),
/// whose one run gives more than one result (<see cref="OperationDescription.ExtraResults"/>): three planes denoised as
/// one colour image, and each handed back; a set of planetary stacks composed into a colour master and a luminance.
/// </summary>
/// <remarks>
/// With fixed inputs, what each input is, is its place among the inputs. A set is handed over sorted by key, so an
/// input's place means nothing and the result must not depend on it. Either way the run is one step's, unlike
/// <see cref="IJointOperation"/>, which puts several images of the project through one run and gives each its own results.
/// </remarks>
public interface IMultiResultOperation : IOperation
{
    /// <summary>The command that does the run from a terminal, program name first: every input and every file it writes.</summary>
    ImmutableArray<string> CommandLine(string canonicalSettings, IReadOnlyList<string> inputs, StepOutputs outputs);

    /// <summary>Does the work and writes the main result and each extra result to the file
    /// <paramref name="outputs"/> names for it. A refusal writes nothing that counts.</summary>
    ValueTask<OperationOutcome> RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs, Action<string>? say,
        CancellationToken cancellationToken);
}

/// <summary>
/// An operation whose results are named by its run (<see cref="OperationDescription.NamedByRun"/>): a stack, which gives
/// a master for each group its scan finds. Its verb writes them into a folder under names of its own, so here the
/// place <see cref="IOperation.CommandLine"/> and <see cref="IOperation.RunAsync"/> take is that folder.
/// </summary>
/// <remarks>
/// The step itself has no image: an edit after it in the same list has nothing to take. An image of the project starts
/// from one of its results by name, as it starts from an extra result of any other step.
/// </remarks>
public interface IRunNamedOperation : IOperation
{
    /// <summary>Does the work into <paramref name="folder"/>, as the verb does into its output folder, and answers
    /// what it wrote there: each result's name, its file, and the files that belong beside it.</summary>
    /// <param name="canonicalSettings">The settings, as <see cref="IOperation.TryReadSettings"/> answered them.</param>
    /// <param name="inputs">The edit list's image first, then the description's extra inputs in order.</param>
    /// <param name="folder">The folder to write into. The caller takes from it what the answer names.</param>
    /// <param name="say">Takes each line the operation reports (what a verb prints), or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    ValueTask<NamedResults> RunIntoAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>As <see cref="RunIntoAsync(string, IReadOnlyList{OperationInput}, string, Action{string}?, CancellationToken)"/>, with
    /// a place to report a fraction in [0, 1]. The runner calls this one; the default reports none.</summary>
    ValueTask<NamedResults> RunIntoAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunIntoAsync(canonicalSettings, inputs, folder, say, cancellationToken);
}

/// <summary>One result of a run that names its own results.</summary>
/// <param name="Name">What an image of the project names to start from it. A run gives no two results one name.</param>
/// <param name="File">The file the run wrote.</param>
/// <param name="Companions">The files that belong beside it and are found by its name: a master's map sidecars and
/// its manifest. Each is named as <paramref name="File"/> is without its ending, with an ending of its own.</param>
public sealed record NamedResult(string Name, string File, ImmutableArray<string> Companions);

/// <summary>What a run that names its own results gave.</summary>
/// <param name="Outcome">Whether it succeeded, and why not.</param>
/// <param name="Results">The results, where it succeeded.</param>
public sealed record NamedResults(OperationOutcome Outcome, ImmutableArray<NamedResult> Results)
{
    /// <summary>A run that gave nothing, and why.</summary>
    public static NamedResults Refused(OperationOutcome outcome) => new(outcome, []);
}

/// <summary>
/// An operation with its settings as a type. A verb builds the settings from its options and calls
/// <see cref="RunAsync"/>; the runner goes through <see cref="IOperation"/> and reaches the same method, so the two
/// cannot differ.
/// </summary>
/// <typeparam name="TSettings">The settings: a class whose properties are settable and carry their defaults, read
/// and written through <see cref="SettingsInfo"/>.</typeparam>
public abstract class Operation<TSettings> : IOperation
    where TSettings : class, new()
{
    /// <inheritdoc/>
    public abstract OperationDescription Description { get; }

    /// <summary>The settings type's source-generated JSON contract (<see cref="OperationSettingsJsonContext"/>).</summary>
    protected abstract JsonTypeInfo<TSettings> SettingsInfo { get; }

    /// <inheritdoc/>
    public JsonTypeInfo? SettingsContract => SettingsInfo;

    /// <inheritdoc/>
    public string DefaultSettings => JsonSerializer.Serialize(new TSettings(), SettingsInfo);

    /// <summary>Says in words why <paramref name="settings"/> cannot be run, or null when they can.</summary>
    public virtual string? Refusal(TSettings settings) => null;

    /// <inheritdoc cref="IOperation.ServedBy"/>
    public virtual string ServedBy(TSettings settings) => string.Empty;

    /// <inheritdoc cref="IOperation.ServedTag"/>
    public virtual string ServedTag(TSettings settings) => string.Empty;

    /// <inheritdoc cref="IOperation.Served"/>
    public virtual (string By, string Tag) Served(TSettings settings) => (ServedBy(settings), ServedTag(settings));

    /// <inheritdoc cref="IOperation.CommandLine"/>
    public abstract ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> inputs, string output);

    /// <inheritdoc cref="IOperation.RunAsync"/>
    public abstract ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>As <see cref="RunAsync(TSettings, IReadOnlyList{OperationInput}, string, Action{string}?, CancellationToken)"/>, reporting a
    /// fraction in [0, 1] where the engine has one. Override this one for an operation with progress; the default reports none.</summary>
    public virtual ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunAsync(settings, inputs, outputPath, say, cancellationToken);

    /// <inheritdoc/>
    public bool TryReadSettings(string? settingsJson, [NotNullWhen(true)] out string? canonical, [NotNullWhen(false)] out string? refusal)
    {
        canonical = null;
        TSettings? settings;
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            settings = new TSettings();
        }
        else
        {
            try
            {
                settings = JsonSerializer.Deserialize(settingsJson, SettingsInfo);
            }
            catch (JsonException ex)
            {
                // The one place a person's hand-written JSON meets the type: a setting this operation does not have, or
                // a value of the wrong kind, is said with the serializer's own words (they name the property).
                refusal = $"the settings of '{Description.Id}' cannot be read: {ex.Message}";
                return false;
            }
        }

        if (settings is null)
        {
            refusal = $"the settings of '{Description.Id}' are null: leave them out for the defaults";
            return false;
        }

        if (Refusal(settings) is { } why)
        {
            refusal = $"the settings of '{Description.Id}' are refused: {why}";
            return false;
        }

        canonical = JsonSerializer.Serialize(settings, SettingsInfo);
        refusal = null;
        return true;
    }

    /// <summary>The settings a canonical text holds. It was written by <see cref="TryReadSettings"/>, so it reads.</summary>
    protected TSettings Parse(string canonicalSettings)
        => JsonSerializer.Deserialize(canonicalSettings, SettingsInfo)
            ?? throw new ArgumentException($"'{canonicalSettings}' holds no settings of '{Description.Id}'", nameof(canonicalSettings));

    string IOperation.ServedBy(string canonicalSettings) => ServedBy(Parse(canonicalSettings));

    string IOperation.ServedTag(string canonicalSettings) => ServedTag(Parse(canonicalSettings));

    (string By, string Tag) IOperation.Served(string canonicalSettings) => Served(Parse(canonicalSettings));

    ImmutableArray<string> IOperation.CommandLine(string canonicalSettings, IReadOnlyList<string> inputs, string output)
        => CommandLine(Parse(canonicalSettings), inputs, output);

    ValueTask<OperationOutcome> IOperation.RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(Parse(canonicalSettings), inputs, outputPath, say, cancellationToken);

    ValueTask<OperationOutcome> IOperation.RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunAsync(Parse(canonicalSettings), inputs, outputPath, say, progress, cancellationToken);
}

/// <summary>An <see cref="Operation{TSettings}"/> several images go through in one run (<see cref="IJointOperation"/>).</summary>
/// <typeparam name="TSettings">The settings, as for <see cref="Operation{TSettings}"/>.</typeparam>
public abstract class JointOperation<TSettings> : Operation<TSettings>, IJointOperation
    where TSettings : class, new()
{
    /// <inheritdoc cref="IJointOperation.CommandLine"/>
    public abstract ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs);

    /// <inheritdoc cref="IJointOperation.RunJointAsync"/>
    public abstract ValueTask<OperationOutcome> RunJointAsync(TSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>One image alone is a run of one.</summary>
    public sealed override ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> inputs, string output)
    {
        IReadOnlyList<StepOutputs> outputs = [new StepOutputs(output)];
        return CommandLine(settings, inputs, outputs);
    }

    /// <summary>One image alone is a run of one; several need a place for each one's result, which
    /// <see cref="RunJointAsync"/> takes.</summary>
    public sealed override ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StepOutputs> outputs = [new StepOutputs(outputPath)];
        return inputs.Count == 1
            ? RunJointAsync(settings, inputs, outputs, say, cancellationToken)
            : ValueTask.FromResult(OperationOutcome.BadInput($"'{Description.Id}' puts several images through one run, and each needs a place for its result"));
    }

    ImmutableArray<string> IJointOperation.CommandLine(string canonicalSettings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
        => CommandLine(Parse(canonicalSettings), members, outputs);

    ValueTask<OperationOutcome> IJointOperation.RunJointAsync(string canonicalSettings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
        => RunJointAsync(Parse(canonicalSettings), members, outputs, say, cancellationToken);
}

/// <summary>An <see cref="Operation{TSettings}"/> whose results are named by its run (<see cref="IRunNamedOperation"/>).</summary>
/// <typeparam name="TSettings">The settings, as for <see cref="Operation{TSettings}"/>.</typeparam>
/// <remarks>The place <see cref="Operation{TSettings}.CommandLine"/> takes is the folder its results go into.</remarks>
public abstract class RunNamedOperation<TSettings> : Operation<TSettings>, IRunNamedOperation
    where TSettings : class, new()
{
    /// <inheritdoc cref="IRunNamedOperation.RunIntoAsync"/>
    public abstract ValueTask<NamedResults> RunIntoAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>As <see cref="RunIntoAsync(TSettings, IReadOnlyList{OperationInput}, string, Action{string}?, CancellationToken)"/>, reporting
    /// a fraction in [0, 1]. An operation with engine progress overrides this one and the routine above calls it with none.</summary>
    public virtual ValueTask<NamedResults> RunIntoAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunIntoAsync(settings, inputs, folder, say, cancellationToken);

    /// <summary>The verb's routine: the results are left in the folder <paramref name="outputPath"/> names, under the
    /// names the run gives them.</summary>
    public sealed override async ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => (await RunIntoAsync(settings, inputs, outputPath, say, cancellationToken)).Outcome;

    ValueTask<NamedResults> IRunNamedOperation.RunIntoAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken)
        => RunIntoAsync(Parse(canonicalSettings), inputs, folder, say, cancellationToken);

    ValueTask<NamedResults> IRunNamedOperation.RunIntoAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => RunIntoAsync(Parse(canonicalSettings), inputs, folder, say, progress, cancellationToken);
}

/// <summary>An <see cref="Operation{TSettings}"/> whose one run gives several results (<see cref="IMultiResultOperation"/>).</summary>
/// <typeparam name="TSettings">The settings, as for <see cref="Operation{TSettings}"/>.</typeparam>
public abstract class MultiResultOperation<TSettings> : Operation<TSettings>, IMultiResultOperation
    where TSettings : class, new()
{
    /// <inheritdoc cref="IMultiResultOperation.CommandLine"/>
    public abstract ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs);

    /// <inheritdoc cref="IMultiResultOperation.RunAsync"/>
    public abstract ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs, Action<string>? say,
        CancellationToken cancellationToken);

    /// <summary>With one place for a result, the main result alone is named.</summary>
    public sealed override ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> inputs, string output)
        => CommandLine(settings, inputs, new StepOutputs(output));

    /// <summary>With one place for a result, the main result alone is written.</summary>
    public sealed override ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(settings, inputs, new StepOutputs(outputPath), say, cancellationToken);

    ImmutableArray<string> IMultiResultOperation.CommandLine(string canonicalSettings, IReadOnlyList<string> inputs, StepOutputs outputs)
        => CommandLine(Parse(canonicalSettings), inputs, outputs);

    ValueTask<OperationOutcome> IMultiResultOperation.RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs,
        Action<string>? say, CancellationToken cancellationToken)
        => RunAsync(Parse(canonicalSettings), inputs, outputs, say, cancellationToken);
}

/// <summary>An <see cref="Operation{TSettings}"/> that writes no pixels (<see cref="IFindingOperation"/>).</summary>
/// <typeparam name="TSettings">The settings, as for <see cref="Operation{TSettings}"/>.</typeparam>
public abstract class FindingOperation<TSettings> : Operation<TSettings>, IFindingOperation
    where TSettings : class, new()
{
    /// <inheritdoc cref="IFindingOperation.FindAsync"/>
    public abstract ValueTask<OperationOutcome> FindAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath, Action<string>? say,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IFindingOperation.Apply"/>
    public abstract OperationInput Apply(TSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath);

    /// <inheritdoc cref="IFindingOperation.Write"/>
    public abstract OperationOutcome Write(TSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath, string outputPath);

    ValueTask<OperationOutcome> IFindingOperation.FindAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath, Action<string>? say,
        CancellationToken cancellationToken)
        => FindAsync(Parse(canonicalSettings), inputs, findingPath, say, cancellationToken);

    OperationInput IFindingOperation.Apply(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath)
        => Apply(Parse(canonicalSettings), inputs, findingPath);

    OperationOutcome IFindingOperation.Write(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string findingPath, string outputPath)
        => Write(Parse(canonicalSettings), inputs, findingPath, outputPath);
}

/// <summary>An <see cref="Operation{TSettings}"/> whose result is derived, never stored.</summary>
/// <typeparam name="TSettings">The settings, as for <see cref="Operation{TSettings}"/>.</typeparam>
public abstract class DerivedOperation<TSettings> : Operation<TSettings>, IDerivedOperation
    where TSettings : class, new()
{
    /// <inheritdoc cref="IDerivedOperation.Derive"/>
    public abstract OperationInput Derive(TSettings settings, IReadOnlyList<OperationInput> inputs);

    OperationInput IDerivedOperation.Derive(string canonicalSettings, IReadOnlyList<OperationInput> inputs)
        => Derive(Parse(canonicalSettings), inputs);
}
