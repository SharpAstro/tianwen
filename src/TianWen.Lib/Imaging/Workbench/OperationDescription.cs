using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>How an operation's output is kept (docs/plans/stacking-workbench.md, "An operation").</summary>
public enum OperationOutputKind
{
    /// <summary>A file in the step store, written once and found again by its key: a gradient fit, a linear fit.</summary>
    Stored,

    /// <summary>Made again from its input whenever it is asked for, and never stored: a crop.</summary>
    Derived,

    /// <summary>No pixels are written (the plan's "metadata"): what the operation finds out about its input is a small
    /// file in the step store, and its result is its input with that applied. A plate solve.</summary>
    Finding,
}

/// <summary>One setting of an operation, as a listing or a panel shows it. Its default is not repeated here: it is the
/// value in <see cref="IOperation.DefaultSettings"/> under the same name.</summary>
/// <param name="Name">The property's name in the settings JSON.</param>
/// <param name="Summary">What it does.</param>
/// <param name="Measurement">What its default rests on, where that was measured (the plan's rule 4); null for a setting
/// with no default to defend, such as a rectangle.</param>
public sealed record OperationSetting(string Name, string Summary, string? Measurement = null);

/// <summary>An image an operation takes besides the one its edit list holds: a linear fit's reference.</summary>
/// <param name="Name">What the input is, for a message and a listing.</param>
/// <param name="Summary">What it is used for.</param>
/// <param name="Optional">True for an input the operation runs without. Optional inputs come last, so an edit that
/// names fewer images leaves out the last ones.</param>
public sealed record OperationInputRole(string Name, string Summary, bool Optional = false);

/// <summary>A result an operation gives beyond its main one: the stars a star removal took out.</summary>
/// <param name="Name">What an image of a project names to start from it (<see cref="ProjectImage.Result"/>).</param>
/// <param name="Summary">What it is.</param>
public sealed record OperationResultRole(string Name, string Summary);

/// <summary>
/// What an operation is, said once: the edit list, the step store, the command line shown under a step and a window's
/// panel all read this.
/// </summary>
/// <param name="Id">The name an edit refers to it by, and part of every step's key.</param>
/// <param name="Version">Part of every step's key. A change that gives other pixels for the same settings is a new
/// version, so an output of the old one is never taken for the new one's.</param>
/// <param name="Title">The name a person reads.</param>
/// <param name="Verb">The command-line verb whose routine this is.</param>
/// <param name="Output">How its output is kept.</param>
/// <param name="ExtraInputs">The images it takes after its edit list's own, in order.</param>
/// <param name="Settings">Its settings.</param>
public sealed record OperationDescription(
    string Id,
    int Version,
    string Title,
    string Verb,
    OperationOutputKind Output,
    ImmutableArray<OperationInputRole> ExtraInputs,
    ImmutableArray<OperationSetting> Settings)
{
    /// <summary>The results it gives beyond its main one, each kept in the store under a key of its own.</summary>
    public ImmutableArray<OperationResultRole> ExtraResults { get; init; } = [];

    /// <summary>
    /// True for an operation several images go through in ONE run, on one scale, each getting its own result
    /// (<see cref="IJointOperation"/>): a deblur, a denoise, a star removal. An edit of such an operation names the
    /// other images of its run, any number of them, and <see cref="ExtraInputs"/> is empty.
    /// </summary>
    public bool Joint { get; init; }
}

/// <summary>Why an operation did not give a result: which of the verbs' exit codes it is.</summary>
public enum OperationFailure
{
    /// <summary>It gave its result.</summary>
    None,

    /// <summary>What it was handed cannot be worked on: a file that will not read, images on different grids, a
    /// rectangle outside the image.</summary>
    Input,

    /// <summary>It ran and failed.</summary>
    Failed,

    /// <summary>Nothing on this computer serves it: a backend that is not installed or not licensed.</summary>
    NotServed,
}

/// <summary>What an operation did: its result written, or a refusal in words.</summary>
/// <param name="Failure">Why there is no result, or <see cref="OperationFailure.None"/>.</param>
/// <param name="Message">Empty on success; on a refusal, what is wrong.</param>
public readonly record struct OperationOutcome(OperationFailure Failure, string Message)
{
    /// <summary>The result was written.</summary>
    public static OperationOutcome Done { get; } = new(OperationFailure.None, string.Empty);

    /// <summary>True when the result was written.</summary>
    public bool Succeeded => Failure is OperationFailure.None;

    /// <summary>The input cannot be worked on, for the reason given.</summary>
    public static OperationOutcome BadInput(string why) => new(OperationFailure.Input, why);

    /// <summary>The operation ran and failed, for the reason given.</summary>
    public static OperationOutcome Failed(string why) => new(OperationFailure.Failed, why);

    /// <summary>Nothing here serves the operation, for the reason given.</summary>
    public static OperationOutcome NotServed(string why) => new(OperationFailure.NotServed, why);
}
