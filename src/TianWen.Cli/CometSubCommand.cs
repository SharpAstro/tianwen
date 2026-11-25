using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen comet track &lt;frames&gt; -o &lt;track.json&gt;</c>: the comet a night of lights was taken of, its track asked of
/// JPL Horizons once from the frames' own site over the whole night, and written as the file
/// <c>tianwen stack --comet-track</c> and the workbench's <c>comet-stack</c> step register on (<see cref="CometTrack"/>,
/// #2489). The routine is Lib's (<see cref="CometTrackFetch"/>); this reads the line and says what was written.
/// </summary>
internal sealed class CometSubCommand(IConsoleHost consoleHost, ILogger<CometSubCommand> logger)
{
    /// <summary>Test seam: asks for the track in place of Horizons, so a test drives the verb with no network.</summary>
    internal Func<CometTrackRequest, CancellationToken, Task<(CometTrack? Track, string? Refusal)>>? Fetch { get; init; }

    public Command Build()
    {
        var framesArg = new Argument<string>("frames")
        {
            Description = "The night's folder of lights, read recursively (headers only): its window is every light's exposure, its site the frames' SITELAT/SITELONG/SITEELEV.",
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "The track file to write (JSON).",
            Required = true,
        };
        var designationOpt = new Option<string?>("--designation")
        {
            Description = "The comet, e.g. 10P or 'C/2025 R2'. Left out, it is read off the lights' OBJECT card, which must name one comet.",
        };

        var track = new Command("track", "Ask JPL Horizons once for the track of the comet a folder of lights was taken of, from the frames' own site over the whole night, and write it as a file a stack registers on with no network (stack --comet-track, the workbench's comet-stack step).")
        {
            Arguments = { framesArg },
            Options = { outputOpt, designationOpt },
        };

        track.SetAction(async (parseResult, ct) =>
        {
            var frames = parseResult.Required(framesArg);
            if (!Directory.Exists(frames))
            {
                consoleHost.WriteError($"there is no folder {frames}");
                return 1;
            }

            if (!CometTrackFetch.TryRequestFor(frames, parseResult.GetValue(designationOpt), out var request, out var lights, out var refusal))
            {
                consoleHost.WriteError(refusal);
                return 1;
            }

            // The folder is made before Horizons is asked: an answer is never lost to a place it cannot be written.
            var output = Path.GetFullPath(parseResult.Required(outputOpt));
            try
            {
                if (Path.GetDirectoryName(output) is { Length: > 0 } outputFolder)
                {
                    Directory.CreateDirectory(outputFolder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                consoleHost.WriteError($"the track cannot be written to {output}: {ex.Message}");
                return 1;
            }

            consoleHost.WriteScrollable(
                $"[comet] {request.Designation} over {lights} light(s), from {request.Start:u} to {request.Stop:u} every {request.Step.TotalMinutes:0} min, "
                + $"seen from ({request.SiteLatDeg:F4}, {request.SiteLonDeg:F4}, {request.SiteElevMetres:F0} m)");
            var (fetched, unanswered) = Fetch is { } fetch
                ? await fetch(request, ct)
                : await CometTrackFetch.FetchAsync(request, logger, ct);
            if (fetched is null)
            {
                consoleHost.WriteError(unanswered ?? $"Horizons gave no track of {request.Designation}");
                return 1;
            }

            await fetched.WriteAsync(output, ct);
            consoleHost.WriteScrollable($"[comet] wrote {output}: {fetched.Samples.Length} positions of {fetched.Designation}");
            return 0;
        });

        return new Command("comet", "Comet tools: the track a comet stack registers on (#2489).")
        {
            Subcommands = { track },
        };
    }
}
