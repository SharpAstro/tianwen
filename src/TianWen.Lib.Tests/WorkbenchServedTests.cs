using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>A stored operation that hands its input on as it is, and counts how often it is asked what serves it.</summary>
internal sealed class ServedOperation : Operation<AddSettings>
{
    public int Asked { get; private set; }

    public override OperationDescription Description { get; } = new(
        "served", Version: 1, Title: "Served", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [],
        Settings: [new OperationSetting("amount", "Not used: two values of it are two settings.")]);

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override string ServedBy(AddSettings settings)
    {
        Asked++;
        return "Test/Backend";
    }

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        OperationOutput.Write(image, outputPath, wcs);
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// What serves a step is in its key, and naming it can cost a licence probe (an RC-Astro role asks whether its product
/// is licensed, #2475's review): a plan asks it once for an operation and its settings, however many steps have them.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchServedTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task WhatServesIsAskedOnceForAnOperationAndItsSettingsInAPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("served-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        JsonObject Amount(int amount) => new() { ["amount"] = amount };
        // Four steps of one operation under two settings, on three images.
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("a", master, WorkbenchFixtures.Edit("served", Amount(1))),
            WorkbenchFixtures.Picture("b", master, WorkbenchFixtures.Edit("served", Amount(1)), WorkbenchFixtures.Edit("served", Amount(1))),
            WorkbenchFixtures.Picture("c", master, WorkbenchFixtures.Edit("served", Amount(2)))), ct);
        var served = new ServedOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([served]));

        var status = await runner.StatusAsync(folder, cancellationToken: ct);

        served.Asked.ShouldBe(2, "two settings are two questions, whatever the number of steps and images");
        status.Images[1].Steps[0].Key.ShouldBe(status.Images[0].Steps[0].Key, "one answer went into every key that asked");

        var run = await runner.RunAsync(folder, cancellationToken: ct);

        run.Succeeded.ShouldBeTrue();
        served.Asked.ShouldBe(4, "the next plan asks again: what serves can change between two plans, and never within one");
    }
}
