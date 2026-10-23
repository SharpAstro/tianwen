using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Cli;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #1778: a PowerShell splat turned <c>--kept-cores</c> into twelve one-letter values of degrade's <c>--session</c>,
/// "e" matched 201 of 201 sessions and the whole bake was exported with no error. The rule is one helper, so these
/// need no bake and no data.
/// </summary>
public class SessionFilterTests : IDisposable
{
    private readonly TempFolders _temp = new();

    private static readonly string[] Sessions =
    [
        "asi2600/m42/lrgb/2026-01-03",
        "asi2600/m42/lrgb/2026-02-11",
        "qhy268/m31/lrgb/2026-01-03",
        "qhy268/vela/ha/2026-03-09",
    ];

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("e")]
    [InlineData("t")]
    [InlineData("m4")]
    [InlineData("")]
    [InlineData("  ")]
    public void AValueUnderThreeCharactersIsRefusedAndNamed(string value)
    {
        SessionFilter.TryValidate([value], out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain($"'{value}'");
        error.ShouldContain(SessionFilter.MinLength.ToString());
        Should.Throw<ArgumentException>(() => SessionFilter.Require([value])).Message.ShouldContain($"'{value}'");
    }

    [Fact]
    public void EveryShortValueOfASplatIsNamed()
    {
        string[] splat = ["m42", "e", "t", "vela", "x"];

        SessionFilter.TryValidate(splat, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("'e'");
        error.ShouldContain("'t'");
        error.ShouldContain("'x'");
        error.ShouldNotContain("'m42'");
        error.ShouldNotContain("'vela'");
    }

    [Fact]
    public void ThreeCharactersOrMoreIsAccepted()
    {
        SessionFilter.TryValidate(["m42", "VELA", "qhy268/m31"], out var error).ShouldBeTrue();
        error.ShouldBeNull();
        SessionFilter.TryValidate([], out _).ShouldBeTrue("no filter is the whole bake, not an error");
        SessionFilter.TryValidate(null, out _).ShouldBeTrue();
    }

    [Fact]
    public void AMultiValueFilterSelectsTheUnionCaseInsensitively()
    {
        var selected = SessionFilter.Select(Sessions, ["M42", "vela"]);

        selected.ShouldBe([Sessions[0], Sessions[1], Sessions[3]]);
        SessionFilter.Select(Sessions, []).ShouldBe(Sessions);
        SessionFilter.Select(Sessions, ["nothing-like-it"]).ShouldBeEmpty();
    }

    [Fact]
    public void TheSelectionIsPrintedWithItsCountAndIds()
    {
        var logger = new CapturingLogger();

        var selected = SessionFilter.SelectAndAnnounce("degrade", Sessions, ["m42", "vela"], logger);

        selected.Count.ShouldBe(3);
        logger.Lines.Count.ShouldBe(4);
        logger.Lines[0].ShouldBe("[degrade] --session 'm42' 'vela' selects 3 of 4 sessions");
        logger.Lines.Skip(1).ShouldBe([$"[degrade]   {Sessions[0]}", $"[degrade]   {Sessions[1]}", $"[degrade]   {Sessions[3]}"]);
    }

    [Fact]
    public void NoFilterIsPrintedAsTheCountAlone()
    {
        var logger = new CapturingLogger();

        SessionFilter.SelectAndAnnounce("noise-check", Sessions, [], logger);

        logger.Lines.ShouldBe(["[noise-check] no --session filter: 4 of 4 sessions"]);
    }

    [Fact]
    public void ARefusedFilterPrintsNothingAndSelectsNothing()
    {
        var logger = new CapturingLogger();

        Should.Throw<ArgumentException>(() => SessionFilter.SelectAndAnnounce("degrade", Sessions, ["e"], logger));

        logger.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDegradeExportRefusesBeforeItWritesAnything()
    {
        var bake = Path.Combine(_temp.Create("sf-").FullName, "bake");
        var outDir = Path.Combine(_temp.Create("sf-").FullName, "out");

        var thrown = await Should.ThrowAsync<ArgumentException>(async () => await DatasetDegradationExporter.RunAsync(
            new DatasetDegradationExporter.Options(bake, outDir, SessionFilters: ["e", "t"]),
            logger: null,
            TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("'e'");
        Directory.Exists(outDir).ShouldBeFalse("a refused run creates no output folder");
    }

    [Fact]
    public async Task TheOtherExportersRefuseToo()
    {
        var bake = Path.Combine(_temp.Create("sf-").FullName, "bake");
        var ct = TestContext.Current.CancellationToken;

        (await Should.ThrowAsync<ArgumentException>(async () =>
            await DatasetDegradationExporter.ListBrightCellsAsync(bake, ["e"], 0, 1, cancellationToken: ct))).Message.ShouldContain("'e'");
        (await Should.ThrowAsync<ArgumentException>(async () =>
            await DatasetDegradationExporter.CheckInjectionAsync(bake, ["t"], 0, 1, null, cancellationToken: ct))).Message.ShouldContain("'t'");
        (await Should.ThrowAsync<ArgumentException>(async () =>
            await DatasetNoisePlaneExporter.RunAsync(
                new DatasetNoisePlaneExporter.Options(bake, _temp.Create("sf-").FullName, ["a"], []), logger: null, ct))).Message.ShouldContain("'a'");
    }

    [Theory]
    [InlineData("degrade")]
    [InlineData("bright-cells")]
    [InlineData("noise-check")]
    [InlineData("noise-planes")]
    public void EveryVerbWithTheOptionFailsTheParseNamingTheValue(string verb)
    {
        var dataset = new DatasetSubCommand(Substitute.For<IConsoleHost>()).Build();

        var refused = dataset.Parse([verb, "--session", "m42", "e", "t"]);
        var accepted = dataset.Parse([verb, "--session", "m42", "vela"]);

        var sessionErrors = refused.Errors.Where(static e => e.Message.Contains("--session", StringComparison.Ordinal)).ToArray();
        sessionErrors.Length.ShouldBe(1);
        sessionErrors[0].Message.ShouldContain("'e'");
        sessionErrors[0].Message.ShouldContain("'t'");
        sessionErrors[0].Message.ShouldNotContain("'m42'");
        accepted.Errors.ShouldNotContain(static e => e.Message.Contains("--session", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
