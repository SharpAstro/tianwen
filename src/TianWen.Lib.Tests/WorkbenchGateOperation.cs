using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stored operation that stands at a gate: it says a line, reports half its work, tells the test it started, and then waits for
/// the test to open the gate or for its cancellation token, which is how a test holds a run in the middle without waiting out a
/// clock. Once through it copies its input.
/// </summary>
internal sealed class GateOperation(string id = "gate") : Operation<AddSettings>
{
    /// <summary>Completes when the operation stands at the gate, its line said and its half reported.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The test completes this to let the operation through.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override OperationDescription Description { get; } = new(
        id, Version: 1, Title: "Gate", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        Summary = "Waits at a gate the test holds, then copies the image.",
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(settings, inputs, outputPath, say, progress: null, cancellationToken);

    public override async ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath,
        Action<string>? say, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        say?.Invoke("at the gate");
        progress?.Report(0.5);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);

        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        OperationOutput.Write(image, outputPath, wcs);
        return OperationOutcome.Done;
    }
}
