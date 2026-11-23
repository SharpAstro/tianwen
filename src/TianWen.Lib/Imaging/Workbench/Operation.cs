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
    public string DefaultSettings => JsonSerializer.Serialize(new TSettings(), SettingsInfo);

    /// <summary>Says in words why <paramref name="settings"/> cannot be run, or null when they can.</summary>
    public virtual string? Refusal(TSettings settings) => null;

    /// <inheritdoc cref="IOperation.ServedBy"/>
    public virtual string ServedBy(TSettings settings) => string.Empty;

    /// <inheritdoc cref="IOperation.CommandLine"/>
    public abstract ImmutableArray<string> CommandLine(TSettings settings, IReadOnlyList<string> inputs, string output);

    /// <inheritdoc cref="IOperation.RunAsync"/>
    public abstract ValueTask<OperationOutcome> RunAsync(TSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken);

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

    ImmutableArray<string> IOperation.CommandLine(string canonicalSettings, IReadOnlyList<string> inputs, string output)
        => CommandLine(Parse(canonicalSettings), inputs, output);

    ValueTask<OperationOutcome> IOperation.RunAsync(string canonicalSettings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(Parse(canonicalSettings), inputs, outputPath, say, cancellationToken);
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
