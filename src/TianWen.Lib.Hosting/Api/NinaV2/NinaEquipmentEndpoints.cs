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
    }

    /// <summary>
    /// Stub endpoints for devices TianWen doesn't support.
    /// TNS gracefully handles { Connected: false }.
    /// </summary>
    private static void MapStubEndpoints(RouteGroupBuilder group)
    {
        var disconnected = ResponseEnvelope<NinaStubInfoDto>.Ok(NinaStubInfoDto.Disconnected);

        foreach (var device in new[] { "rotator", "flatdevice", "dome", "switch", "weather", "safetymonitor" })
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
