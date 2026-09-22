using System;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

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
/// </summary>
internal sealed class DarksSubCommand(IConsoleHost consoleHost, IDeviceHub deviceHub, DarkFrameRun darkFrameRun)
{
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

            var cameras = (await consoleHost.ListAllDevicesAsync(DeviceDiscoveryOption.None, ct))
                .Where(d => d.DeviceType is DeviceType.Camera)
                .ToList();

            if (cameras.Count == 0)
            {
                consoleHost.WriteScrollable("No camera found.");
                return 1;
            }

            var wanted = parseResult.GetValue(cameraOpt);
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
            var options = new DarkFrameRunOptions(
                TimeSpan.FromSeconds(exposureSeconds),
                parseResult.GetValue(countOpt),
                parseResult.GetValue(gainOpt) is { } g ? (short)g : null,
                parseResult.GetValue(offsetOpt),
                parseResult.GetValue(binOpt),
                frameType);

            consoleHost.WriteScrollable(
                $"[{frameType.ToString().ToLowerInvariant()}] {device.DisplayName}: {options.Count} x {options.Exposure.TotalSeconds:0.#####}s"
                + (options.Gain is { } gv ? $", gain {gv}" : "")
                + (options.Offset is { } ov ? $", offset {ov}" : "")
                + $", bin {options.Bin}");

            var driver = await deviceHub.ConnectAsync(device, ct);
            if (driver is not ICameraDriver camera)
            {
                consoleHost.WriteScrollable($"{device.DisplayName} did not connect as a camera.");
                return 1;
            }

            var tag = frameType.ToString().ToLowerInvariant();

            try
            {
                var progress = new Progress<DarkFrameCaptured>(f =>
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[{tag}] {f.SensorTemperatureC:0.0} C  {System.IO.Path.GetFileName(f.Path)}")));

                var captured = await darkFrameRun.RunAsync(camera, options, progress, ct);

                if (captured.Count > 0)
                {
                    var temps = captured.Select(c => c.SensorTemperatureC).ToList();
                    var min = temps.Min();
                    var max = temps.Max();
                    var mean = temps.Average();

                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"\n[{tag}] {captured.Count} frame(s), sensor {mean:0.00} C mean, {min:0.0} to {max:0.0} C, span {max - min:0.00} C"));

                    // The spread is the thing a caller has to judge, not a number to bury in a log: on
                    // an unregulated body it decides whether this is one library row or several.
                    consoleHost.WriteScrollable(max - min <= 0.3
                        ? $"[{tag}] spread is within a regulated body's own stability; treat as one row."
                        : $"[{tag}] spread is wider than a regulated body's; these frames do not all describe the same temperature.");
                }

                return 0;
            }
            finally
            {
                await deviceHub.DisconnectAsync(device.DeviceUri, cancellationToken: ct);
            }
        });

        return darksCommand;
    }
}
