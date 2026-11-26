using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// A broadband SPCC white balance multiplied into a colour master's linear pixels (<see cref="Image.WithWhiteBalanceApplied"/>):
/// the step the enhance runs first (<see cref="Enhancement.WhiteBalanceStep"/>), alone, so a chain of single steps does what the
/// enhance does. Which balance is the enhance's own rule (<see cref="EnhanceOperation.WhiteBalanceToApplyAsync"/>): a stated
/// triple, else the one the master's WB cards state, and only a broadband SPCC one. <c>image white-balance</c> calls this.
/// </summary>
/// <remarks>
/// The result says its colour is balanced in its pixels (<c>WBAPPLD</c>), which is what SCNR on its stars needs
/// (<see cref="Image.ScnrRefusal"/>). An input with no balance to apply is refused in words, never passed through unbalanced.
/// </remarks>
public sealed class WhiteBalanceOperation : Operation<WhiteBalanceSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "white-balance";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "White balance",
        Verb: "image white-balance",
        OperationOutputKind.Stored,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("whiteBalance", "An SPCC white balance as three numbers (red, green, blue). Left out: the balance the master's "
                + "own WB cards state. Only a broadband one is put into the pixels; a line-selective filter's stays a display multiplier."),
        ])
    {
        Summary = "Puts a broadband white balance into the pixels of a colour master, so red, green and blue agree about what is neutral.",
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<WhiteBalanceSettings> SettingsInfo => OperationSettingsJsonContext.Default.WhiteBalanceSettings;

    /// <inheritdoc/>
    public override string? Refusal(WhiteBalanceSettings settings) => EnhanceOperation.WhiteBalanceRefusal(settings.WhiteBalance);

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(WhiteBalanceSettings settings, IReadOnlyList<string> inputs, string output)
    {
        ImmutableArray<string> line = [StepCommandLine.Program, "image", "white-balance", inputs[0], "-o", output];
        return settings.WhiteBalance.IsDefaultOrEmpty
            ? line
            : line.AddRange("--white-balance", string.Join(',', settings.WhiteBalance.Select(StepCommandLine.Number)));
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(WhiteBalanceSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var source, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        if (source.ChannelCount < 3)
        {
            return OperationOutcome.BadInput($"{inputs[0].Name} is not colour, and a white balance is a colour frame's");
        }

        if (await EnhanceOperation.WhiteBalanceToApplyAsync(source.ImageMeta, settings.WhiteBalance, say, cancellationToken) is not { } balance)
        {
            return OperationOutcome.BadInput(
                $"{inputs[0].Name} has no broadband SPCC white balance to put into its pixels: it states none (or a sky-background or "
                + "line-selective one, which stays a display multiplier), and none was given");
        }

        var balanced = source.WithWhiteBalanceApplied(balance);
        OperationOutput.Write(balanced, outputPath, wcs);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"wrote {outputPath}"));
        return OperationOutcome.Done;
    }
}
