using System;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen darks</c> -- capture dark frames on-demand, outside a session. Connects one camera and
/// nothing else, captures N frames at a stated exposure, gain and offset, and writes them under
/// <c>&lt;output&gt;/Darks/&lt;date&gt;/</c> with <c>IMAGETYP = Dark</c> and each frame's own measured
/// <c>CCD-TEMP</c>.
///
/// <para>The counterpart to <c>tianwen flats</c>, and simpler: a dark needs no mount, no cover, no
/// filter wheel and no metering. What it does need is to MATCH the lights it will calibrate, so
/// exposure, gain, offset and binning are stated rather than inferred, and the run reports the
/// temperature spread it actually achieved instead of a setpoint it was asked for.</para>
///
/// <para><b>Nothing here darkens the sensor.</b> Most CMOS astro cameras have no mechanical shutter,
/// so the operator caps the telescope; the frame type is what the stacker matches on.</para>
///
/// <para>The library is this computer's node's run (P5 part 1 and P6 of docs/plans/hardware-in-the-server.md, #936): the
/// node leases the camera for it, so a session cannot take it meanwhile and a device command is refused naming the dark
/// library. This connects the camera on the node when it is not, follows the run, and gives back what it connected.</para>
/// </summary>
internal sealed class DarksSubCommand(IConsoleHost consoleHost)
{
    /// <summary>How often the node's run is read for the frames it has taken.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Default bias exposure: 10 microseconds, the shortest a Player One body accepts.
    /// </summary>
    /// <remarks>
    /// A camera with a longer minimum clamps up to its own, which is correct: a bias is "as short as
    /// this sensor goes", not a specific duration, and the frame records what it actually got. It is
    /// a default rather than a discovered value because <see cref="ICameraDriver"/> exposes an
    /// exposure RESOLUTION but no minimum, so there is nothing to ask; <c>--exposure</c> overrides it.
    /// </remarks>
    private const double BiasExposureSeconds = 0.00001;

    public Command Build()
    {
        var exposureOpt = new Option<double?>("--exposure")
        {
            Description = "Exposure per frame in seconds. Must match the lights exactly; dark current accumulates with time. Required unless --bias, which defaults to the shortest exposure.",
        };
        var biasOpt = new Option<bool>("--bias")
        {
            Description = "Capture BIAS frames instead of darks: the shortest exposure the camera accepts, labelled IMAGETYP = Bias.",
        };
        var countOpt = new Option<int>("--count")
        {
            Description = "Number of frames to capture (default 20).",
            DefaultValueFactory = _ => 20,
        };
        var gainOpt = new Option<int?>("--gain")
        {
            Description = "Camera gain. Leave unset to keep the camera's current value.",
        };
        var offsetOpt = new Option<int?>("--offset")
        {
            Description = "Camera offset / black level. Leave unset to keep the camera's current value.",
        };
        var binOpt = new Option<int>("--bin")
        {
            Description = "Binning (default 1).",
            DefaultValueFactory = _ => 1,
        };
        var cameraOpt = new Option<string?>("--camera")
        {
            Description = "Which camera, matched against its display name or serial. Optional when exactly one is present.",
        };

        var darksCommand = new Command("darks", "Capture dark or bias frames on-demand from one camera.")
        {
            Options = { exposureOpt, biasOpt, countOpt, gainOpt, offsetOpt, binOpt, cameraOpt },
        };

        darksCommand.SetAction(async (parseResult, ct) =>
        {
            var isBias = parseResult.GetValue(biasOpt);
            var frameType = isBias ? FrameType.Bias : FrameType.Dark;

            // A bias has no exposure to match, only a shortest one, so it defaults. A dark has
            // nothing sensible to default TO: it is defined by matching the light, and a made-up
            // duration would produce files that calibrate nothing while looking complete.
            var exposureSeconds = parseResult.GetValue(exposureOpt) ?? (isBias ? BiasExposureSeconds : 0);
            if (exposureSeconds <= 0)
            {
                consoleHost.WriteScrollable(isBias
                    ? "--exposure must be greater than zero"
                    : "--exposure is required for darks (it must match the lights exactly)");
                return 1;
            }

            // A camera named on the command line is meant, fake or not; unnamed, only real ones are candidates.
            var wanted = parseResult.GetValue(cameraOpt);
            var cameras = (await consoleHost.ListAllDevicesAsync(
                    wanted is { Length: > 0 } ? DeviceDiscoveryOption.IncludeFake : DeviceDiscoveryOption.None, ct))
                .Where(d => d.DeviceType is DeviceType.Camera)
                .ToList();

            if (cameras.Count == 0)
            {
                consoleHost.WriteScrollable("No camera found.");
                return 1;
            }

            var selected = wanted is { Length: > 0 }
                ? cameras.Where(c => c.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                     || c.DeviceId.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList()
                : cameras;

            if (selected.Count != 1)
            {
                // Refuse rather than pick. Shooting a dark library against the wrong body produces
                // files that look correct and calibrate nothing.
                consoleHost.WriteScrollable(selected.Count == 0
                    ? $"No camera matched '{wanted}'. Present: {string.Join(", ", cameras.Select(c => c.DisplayName))}"
                    : $"--camera matched {selected.Count} cameras: {string.Join(", ", selected.Select(c => c.DisplayName))}. Narrow it.");
                return 1;
            }

            var device = selected[0];
            if (await consoleHost.NodeAsync(ct) is not { } node)
            {
                return 1;
            }

            var request = new DarkLibraryRequestDto
            {
                DeviceUri = device.DeviceUri.ToString(),
                ExposureSeconds = exposureSeconds,
                Count = parseResult.GetValue(countOpt),
                Gain = parseResult.GetValue(gainOpt) is { } g ? (short)g : null,
                Offset = parseResult.GetValue(offsetOpt),
                Bin = parseResult.GetValue(binOpt),
                Bias = isBias,
            };
            var tag = frameType.ToString().ToLowerInvariant();
            consoleHost.WriteScrollable(
                $"[{tag}] {device.DisplayName}: {request.Count} x {request.ExposureSeconds:0.#####}s"
                + (request.Gain is { } gv ? $", gain {gv}" : "")
                + (request.Offset is { } ov ? $", offset {ov}" : "")
                + $", bin {request.Bin}");

            var connectedHere = false;
            if (!await IsConnectedAsync(node, device.DeviceUri, ct))
            {
                if (await RunJobAsync(node, node.ConnectDeviceAsync(device.DeviceUri, ct), ct) is { } connectFailure)
                {
                    consoleHost.WriteScrollable($"{device.DisplayName} did not connect: {connectFailure}");
                    return 1;
                }
                connectedHere = true;
            }

            try
            {
                var started = await node.StartDarkLibraryAsync(request, ct);
                if (!started.IsSuccess)
                {
                    consoleHost.WriteScrollable($"[{tag}] the node did not start the library: {started.Error}");
                    return 1;
                }

                if (await FollowAsync(node, tag, ct) is not { } ended)
                {
                    return 1;
                }

                var temps = ended.Frames.Select(static f => f.SensorTemperatureC).OfType<double>().ToList();
                if (temps.Count > 0)
                {
                    var min = temps.Min();
                    var max = temps.Max();
                    var mean = temps.Average();

                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"\n[{tag}] {ended.Frames.Length} frame(s), sensor {mean:0.00} C mean, {min:0.0} to {max:0.0} C, span {max - min:0.00} C"));

                    // The spread is the thing a caller has to judge, not a number to bury in a log: on
                    // an unregulated body it decides whether this is one library row or several.
                    consoleHost.WriteScrollable(max - min <= 0.3
                        ? $"[{tag}] spread is within a regulated body's own stability; treat as one row."
                        : $"[{tag}] spread is wider than a regulated body's; these frames do not all describe the same temperature.");
                }

                if (ended.FailureReason is { } reason)
                {
                    consoleHost.WriteError($"[{tag}] {reason}");
                    return 2;
                }
                return 0;
            }
            finally
            {
                if (connectedHere && await RunJobAsync(node, node.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: false, CancellationToken.None),
                    CancellationToken.None) is { } disconnectFailure)
                {
                    consoleHost.WriteScrollable($"{device.DisplayName} was left connected on the node: {disconnectFailure}");
                }
            }
        });

        return darksCommand;
    }

    // The node's run followed to its end, each frame said as it lands; a Ctrl+C stops the run on the node.
    private async Task<DarkLibraryStateDto?> FollowAsync(TianWenNodeClient node, string tag, CancellationToken cancellationToken)
    {
        var said = 0;
        try
        {
            while (true)
            {
                var now = await node.GetDarkLibraryAsync(cancellationToken);
                if (now.Value is not { } state)
                {
                    consoleHost.WriteScrollable($"[{tag}] the node lost the run: {now.Error}");
                    return null;
                }
                for (; said < state.Frames.Length; said++)
                {
                    var frame = state.Frames[said];
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[{tag}] {frame.SensorTemperatureC:0.0} C  {System.IO.Path.GetFileName(frame.Path)}"));
                }
                if (!state.Running)
                {
                    return state;
                }
                await consoleHost.TimeProvider.SleepAsync(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user stopped the command: the library is the node's run, so it is stopped there, not left going.
            var stopped = await node.StopDarkLibraryAsync(CancellationToken.None);
            consoleHost.WriteScrollable($"[{tag}] stopped after {stopped.Value?.Frames.Length ?? said} frame(s)");
            throw;
        }
    }

    private static async Task<bool> IsConnectedAsync(TianWenNodeClient node, Uri deviceUri, CancellationToken cancellationToken)
        => (await node.GetDeviceStatesAsync(cancellationToken)).Value is { } states
            && states.Any(s => s.Connected && DeviceBase.SameDevice(new Uri(s.DeviceUri), deviceUri));

    // A device job on the node, followed to its end: null once it succeeded, else why not.
    private async Task<string?> RunJobAsync(TianWenNodeClient node, Task<NodeResult<JobDto>> start, CancellationToken cancellationToken)
    {
        var started = await start;
        if (started.Value is not { } job)
        {
            return started.Error ?? "the node did not take it";
        }
        var ended = await node.UntilEndedAsync(job, consoleHost.TimeProvider, cancellationToken);
        return ended.Value is { State: JobState.Succeeded } ? null : ended.Value?.Error ?? ended.Error ?? ended.Value?.State.ToString();
    }
}
