using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Hosting.Dto.NinaV2;

namespace TianWen.Lib.Hosting.Api.NinaV2;

/// <summary>
/// ninaAPI v2 equipment endpoints. All use GET (even actions) per ninaAPI convention.
/// Equipment maps to OTA[0] (single-OTA assumption matching NINA's model).
/// </summary>
internal static class NinaEquipmentEndpoints
{
    public static RouteGroupBuilder MapNinaEquipmentApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v2/api/equipment");

        MapCameraEndpoints(group);
        MapMountEndpoints(group);
        MapFocuserEndpoints(group);
        MapFilterWheelEndpoints(group);
        MapGuiderEndpoints(group);
        MapWeatherEndpoints(group);
        MapDeviceLifecycleEndpoints(group);
        MapStubEndpoints(group);

        return group;
    }

    private static void MapCameraEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/camera/info
        group.MapGet("/camera/info", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session || session.Setup.Telescopes.Length == 0)
            {
                return Results.Json(
                    ResponseEnvelope<NinaCameraInfoDto>.Ok(NinaCameraInfoDto.Disconnected),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaCameraInfoDto);
            }

            var cam = session.Setup.Telescopes[0].Camera.Driver;
            var dto = cam.Connected
                ? await NinaCameraInfoDto.FromDriverAsync(cam, ct)
                : NinaCameraInfoDto.Disconnected;

            return Results.Json(
                ResponseEnvelope<NinaCameraInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaCameraInfoDto);
        });

        // GET /v2/api/equipment/camera/abort-exposure
        group.MapGet("/camera/abort-exposure", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session || session.Setup.Telescopes.Length == 0)
            {
                return NinaFail("No camera available");
            }

            var cam = session.Setup.Telescopes[0].Camera.Driver;
            if (!cam.Connected || !cam.CanAbortExposure)
            {
                return NinaFail("Camera cannot abort exposure");
            }

            await cam.AbortExposureAsync(ct);
            return NinaOk("Exposure aborted");
        });

        // GET /v2/api/equipment/camera/cool?temperature=&minutes= or ?cancel=true
        group.MapGet("/camera/cool", async (IHostedSession hosted, double? temperature, int? minutes, bool? cancel, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session || session.Setup.Telescopes.Length == 0)
            {
                return NinaFail("No camera available");
            }

            var cam = session.Setup.Telescopes[0].Camera.Driver;
            if (!cam.Connected || !cam.CanSetCCDTemperature)
            {
                return NinaFail("Camera does not support cooling");
            }

            if (cancel == true)
            {
                await cam.SetCoolerOnAsync(false, ct);
                return NinaOk("Cooling cancelled");
            }

            if (temperature.HasValue)
            {
                await cam.SetSetCCDTemperatureAsync(temperature.Value, ct);
                await cam.SetCoolerOnAsync(true, ct);
                return NinaOk($"Cooling to {temperature.Value}°C");
            }

            return NinaFail("Missing temperature parameter");
        });

        // GET /v2/api/equipment/camera/warm?minutes= or ?cancel=true
        group.MapGet("/camera/warm", async (IHostedSession hosted, int? minutes, bool? cancel, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session || session.Setup.Telescopes.Length == 0)
            {
                return NinaFail("No camera available");
            }

            var cam = session.Setup.Telescopes[0].Camera.Driver;
            if (!cam.Connected || !cam.CanSetCoolerOn)
            {
                return NinaFail("Camera does not support warming");
            }

            if (cancel == true)
            {
                return NinaOk("Warming cancelled");
            }

            await cam.SetCoolerOnAsync(false, ct);
            return NinaOk("Warming started");
        });
    }

    private static void MapMountEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/mount/info
        group.MapGet("/mount/info", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<NinaMountInfoDto>.Ok(NinaMountInfoDto.Disconnected),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaMountInfoDto);
            }

            var mount = session.Setup.Mount.Driver;
            var dto = mount.Connected
                ? await NinaMountInfoDto.FromDriverAsync(mount, session.MountState, ct)
                : NinaMountInfoDto.Disconnected;

            return Results.Json(
                ResponseEnvelope<NinaMountInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaMountInfoDto);
        });

        // GET /v2/api/equipment/mount/slew?ra=&dec=
        group.MapGet("/mount/slew", async (IHostedSession hosted, double ra, double dec, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanSlew)
            {
                return NinaFail("Mount cannot slew");
            }

            await mount.BeginSlewRaDecAsync(ra, dec, ct);
            return NinaOk("Slew started");
        });

        // GET /v2/api/equipment/mount/slew/stop
        group.MapGet("/mount/slew/stop", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            await session.Setup.Mount.Driver.AbortSlewAsync(ct);
            return NinaOk("Slew stopped");
        });

        // GET /v2/api/equipment/mount/park
        group.MapGet("/mount/park", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanPark)
            {
                return NinaFail("Mount cannot park");
            }

            await mount.ParkAsync(ct);
            return NinaOk("Park started");
        });

        // GET /v2/api/equipment/mount/unpark
        group.MapGet("/mount/unpark", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanUnpark)
            {
                return NinaFail("Mount cannot unpark");
            }

            await mount.UnparkAsync(ct);
            return NinaOk("Mount unparked");
        });

        // GET /v2/api/equipment/mount/tracking?mode=<0-4>
        // mode: 0=None, 1=Sidereal, 2=Lunar, 3=Solar, 4=King
        group.MapGet("/mount/tracking", async (IHostedSession hosted, int mode, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanSetTracking)
            {
                return NinaFail("Mount cannot set tracking");
            }

            var speed = (TrackingSpeed)mode;
            if (speed == TrackingSpeed.None)
            {
                await mount.SetTrackingAsync(false, ct);
            }
            else
            {
                await mount.SetTrackingAsync(true, ct);
                await mount.SetTrackingSpeedAsync(speed, ct);
            }

            return NinaOk($"Tracking set to {speed}");
        });

        // GET /v2/api/equipment/mount/move-axis?direction=<N|S|E|W>&rate=<0-8>
        // direction: N/S = Secondary (Dec), E/W = Primary (RA)
        // rate: index into AxisRates array (0 = slowest guide rate)
        group.MapGet("/mount/move-axis", async (IHostedSession hosted, string direction, int rate, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected)
            {
                return NinaFail("Mount not connected");
            }

            var axis = direction is "N" or "S" ? TelescopeAxis.Seconary : TelescopeAxis.Primary;
            if (!mount.CanMoveAxis(axis))
            {
                return NinaFail($"Mount cannot move {axis} axis");
            }

            var rates = mount.AxisRates(axis);
            if (rate < 0 || rate >= rates.Count)
            {
                return NinaFail($"Rate index {rate} out of range (0-{rates.Count - 1})");
            }

            // Negative rate for S/W direction
            var rateValue = rates[rate].Maximum;
            if (direction is "S" or "W")
            {
                rateValue = -rateValue;
            }

            await mount.MoveAxisAsync(axis, rateValue, ct);
            return NinaOk($"Moving {direction} at rate {rate}");
        });

        // GET /v2/api/equipment/mount/move-axis/stop
        group.MapGet("/mount/move-axis/stop", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected)
            {
                return NinaFail("Mount not connected");
            }

            // Stop both axes
            if (mount.CanMoveAxis(TelescopeAxis.Primary))
            {
                await mount.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
            }
            if (mount.CanMoveAxis(TelescopeAxis.Seconary))
            {
                await mount.MoveAxisAsync(TelescopeAxis.Seconary, 0, ct);
            }

            return NinaOk("Axis motion stopped");
        });
    }

    private static void MapFocuserEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/focuser/info
        group.MapGet("/focuser/info", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session
                || session.Setup.Telescopes.Length == 0
                || session.Setup.Telescopes[0].Focuser is not { } focuser)
            {
                return Results.Json(
                    ResponseEnvelope<NinaFocuserInfoDto>.Ok(NinaFocuserInfoDto.Disconnected),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaFocuserInfoDto);
            }

            var driver = focuser.Driver;
            var dto = driver.Connected
                ? await NinaFocuserInfoDto.FromDriverAsync(driver, ct)
                : NinaFocuserInfoDto.Disconnected;

            return Results.Json(
                ResponseEnvelope<NinaFocuserInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaFocuserInfoDto);
        });

        // GET /v2/api/equipment/focuser/move?position=
        group.MapGet("/focuser/move", async (IHostedSession hosted, int position, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session
                || session.Setup.Telescopes.Length == 0
                || session.Setup.Telescopes[0].Focuser is not { } focuser)
            {
                return NinaFail("No focuser available");
            }

            if (!focuser.Driver.Connected)
            {
                return NinaFail("Focuser not connected");
            }

            await focuser.Driver.BeginMoveAsync(position, ct);
            return NinaOk($"Moving to {position}");
        });
    }

    private static void MapFilterWheelEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/filterwheel/info
        group.MapGet("/filterwheel/info", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session
                || session.Setup.Telescopes.Length == 0
                || session.Setup.Telescopes[0].FilterWheel is not { } fw)
            {
                return Results.Json(
                    ResponseEnvelope<NinaFilterWheelInfoDto>.Ok(NinaFilterWheelInfoDto.Disconnected),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaFilterWheelInfoDto);
            }

            var driver = fw.Driver;
            var dto = driver.Connected
                ? await NinaFilterWheelInfoDto.FromDriverAsync(driver, ct)
                : NinaFilterWheelInfoDto.Disconnected;

            return Results.Json(
                ResponseEnvelope<NinaFilterWheelInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaFilterWheelInfoDto);
        });

        // GET /v2/api/equipment/filterwheel/change-filter?filterId=
        group.MapGet("/filterwheel/change-filter", async (IHostedSession hosted, int filterId, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session
                || session.Setup.Telescopes.Length == 0
                || session.Setup.Telescopes[0].FilterWheel is not { } fw)
            {
                return NinaFail("No filter wheel available");
            }

            if (!fw.Driver.Connected)
            {
                return NinaFail("Filter wheel not connected");
            }

            await fw.Driver.BeginMoveAsync(filterId, ct);
            return NinaOk($"Changing to filter {filterId}");
        });
    }

    private static void MapGuiderEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/guider/info
        group.MapGet("/guider/info", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<NinaGuiderInfoDto>.Ok(NinaGuiderInfoDto.Disconnected),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaGuiderInfoDto);
            }

            var guider = session.Setup.Guider.Driver;
            var dto = new NinaGuiderInfoDto
            {
                Connected = guider.Connected,
                Name = guider.Name,
                State = session.GuiderState ?? (guider.Connected ? "Idle" : "Disconnected"),
            };

            return Results.Json(
                ResponseEnvelope<NinaGuiderInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaGuiderInfoDto);
        });

        // GET /v2/api/equipment/guider/start?calibrate=
        group.MapGet("/guider/start", async (IHostedSession hosted, bool? calibrate, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            var guider = session.Setup.Guider.Driver;
            if (!guider.Connected)
            {
                return NinaFail("Guider not connected");
            }

            if (calibrate == true)
            {
                await guider.ClearCalibrationAsync(ct);
            }

            await guider.GuideAsync(0.3, 3.0, 60.0, ct);
            return NinaOk("Guiding started");
        });

        // GET /v2/api/equipment/guider/stop
        group.MapGet("/guider/stop", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            await session.Setup.Guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(10), ct);
            return NinaOk("Guiding stopped");
        });

        // GET /v2/api/equipment/guider/graph — guide error samples for the guiding graph
        group.MapGet("/guider/graph", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<NinaGuideStepDto[]>.Ok([]),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaGuideStepDtoArray);
            }

            var samples = session.GuideSamples;
            var steps = new NinaGuideStepDto[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                steps[i] = new NinaGuideStepDto
                {
                    Timestamp = s.Timestamp.ToString("o"),
                    RADistanceRawDisplay = s.RaError,
                    DECDistanceRawDisplay = s.DecError,
                    RADuration = s.RaCorrectionMs,
                    DECDuration = s.DecCorrectionMs,
                    Dither = s.IsDither,
                    Settling = s.IsSettling,
                };
            }

            return Results.Json(
                ResponseEnvelope<NinaGuideStepDto[]>.Ok(steps),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaGuideStepDtoArray);
        });

        // GET /v2/api/equipment/guider/clear-calibration
        group.MapGet("/guider/clear-calibration", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NinaFail("No active session");
            }

            await session.Setup.Guider.Driver.ClearCalibrationAsync(ct);
            return NinaOk("Calibration cleared");
        });
    }

    private static void MapWeatherEndpoints(RouteGroupBuilder group)
    {
        // GET /v2/api/equipment/weather/info — read from session's weather device
        group.MapGet("/weather/info", (IHostedSession hosted) =>
        {
            var driver = hosted.CurrentSession?.Setup.Weather?.Driver;
            var dto = driver is { Connected: true }
                ? NinaWeatherInfoDto.FromDriver(driver)
                : NinaWeatherInfoDto.Disconnected;

            return Results.Json(
                ResponseEnvelope<NinaWeatherInfoDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaWeatherInfoDto);
        });
    }

    /// <summary>
    /// Device lifecycle endpoints: list-devices, connect, disconnect, rescan.
    /// In TianWen's model, devices are pre-connected via profile. These endpoints
    /// return the current device as already-connected so TNS can proceed past its
    /// device selection screen.
    /// </summary>
    private static void MapDeviceLifecycleEndpoints(RouteGroupBuilder group)
    {
        // For each device type, list-devices returns the currently active device (or empty).
        // connect/disconnect are acknowledged but no-op (devices are managed by the session).
        foreach (var device in new[] { "camera", "mount", "focuser", "filterwheel", "guider",
                                       "rotator", "flatdevice", "dome", "switch", "weather", "safetymonitor" })
        {
            group.MapGet($"/{device}/list-devices", (IHostedSession hosted) =>
            {
                var name = GetDeviceName(hosted, device);
                var devices = name is not null
                    ? new[] { new { Id = name, DisplayName = name } }
                    : Array.Empty<object>();

                return Results.Json(
                    ResponseEnvelope<object>.Ok(devices),
                    NinaApiJsonContext.Default.ResponseEnvelopeObject);
            });

            group.MapGet($"/{device}/rescan", (IHostedSession hosted) =>
            {
                var name = GetDeviceName(hosted, device);
                var devices = name is not null
                    ? new[] { new { Id = name, DisplayName = name } }
                    : Array.Empty<object>();

                return Results.Json(
                    ResponseEnvelope<object>.Ok(devices),
                    NinaApiJsonContext.Default.ResponseEnvelopeObject);
            });

            group.MapGet($"/{device}/connect", () => NinaOk("Connected"));
            group.MapGet($"/{device}/disconnect", () => NinaOk("Disconnected"));
        }
    }

    private static string? GetDeviceName(IHostedSession hosted, string device)
    {
        if (hosted.CurrentSession is not { } session)
        {
            return null;
        }

        return device switch
        {
            "camera" when session.Setup.Telescopes.Length > 0 => session.Setup.Telescopes[0].Camera.Driver.Name,
            "mount" => session.Setup.Mount.Driver.Name,
            "focuser" when session.Setup.Telescopes.Length > 0 => session.Setup.Telescopes[0].Focuser?.Driver.Name,
            "filterwheel" when session.Setup.Telescopes.Length > 0 => session.Setup.Telescopes[0].FilterWheel?.Driver.Name,
            "guider" => session.Setup.Guider.Driver.Name,
            "weather" => session.Setup.Weather?.Driver.Name,
            _ => null,
        };
    }

    /// <summary>
    /// Stub endpoints for devices TianWen doesn't support.
    /// TNS gracefully handles { Connected: false }.
    /// </summary>
    private static void MapStubEndpoints(RouteGroupBuilder group)
    {
        var disconnected = ResponseEnvelope<NinaStubInfoDto>.Ok(NinaStubInfoDto.Disconnected);

        foreach (var device in new[] { "rotator", "flatdevice", "dome", "switch", "safetymonitor" })
        {
            group.MapGet($"/{device}/info", () => Results.Json(
                disconnected,
                NinaApiJsonContext.Default.ResponseEnvelopeNinaStubInfoDto));
        }
    }

    private static IResult NinaOk(string message)
    {
        return Results.Json(
            ResponseEnvelope<string>.Ok(message),
            NinaApiJsonContext.Default.ResponseEnvelopeString);
    }

    private static IResult NinaFail(string error, int statusCode = 400)
    {
        return Results.Json(
            ResponseEnvelope<string>.Fail(error, statusCode),
            NinaApiJsonContext.Default.ResponseEnvelopeString);
    }
}
