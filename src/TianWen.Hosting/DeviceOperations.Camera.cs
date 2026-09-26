using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// A camera's cooling and settings with no session running, the device plane's third part (P2 of
/// docs/plans/hardware-in-the-server.md, #929). Cooling and warming are jobs, as ramps; switching the cooler off and
/// changing gain, offset, binning or frame are immediate, and refused while a job holds the camera, whose ramp they would
/// fight.
/// </summary>
internal sealed partial class DeviceOperations
{
    internal const string CoolJob = "cool";
    internal const string WarmJob = "warm";

    /// <summary>
    /// Cools the camera to the setpoint through the session's own ramp (<see cref="CameraCoolingRamp"/>), never as a jump,
    /// recording the target as the camera's cooler intent, which the node's crash journal keeps.
    /// </summary>
    public ResponseEnvelope<JobDto> Cool(CoolRequestDto request)
    {
        if (!TryDriver<ICameraDriver>(request.DeviceUri, "camera", out var uri, out var camera, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        if (!camera.CanSetCCDTemperature)
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} cannot set its sensor temperature");
        }
        if (!double.IsFinite(request.SetpointC) || request.RampMinutes is { } minutes && !(minutes > 0 && double.IsFinite(minutes)))
        {
            return ResponseEnvelope<JobDto>.Fail("A setpoint must be a temperature, and a ramp a positive number of minutes");
        }

        var target = CameraCoolingRamp.TargetOf(request.SetpointC);
        var ramp = request.RampMinutes is { } rampMinutes ? TimeSpan.FromMinutes(rampMinutes) : new SessionConfiguration().CooldownRampInterval;
        return Start(CoolJob, uri, name, async (step, ct) =>
        {
            step.Report($"Cooling {name} to {target:F0} °C");
            return await hub.CoolToSetpointAsync(uri, target, ramp, timeProvider, logger, ct)
                ? $"{name} at {target:F0} °C"
                : $"{name} stopped short of {target:F0} °C";
        });
    }

    /// <summary>Warms the camera through the hub's ramp and turns its cooler off, leaving it connected.</summary>
    public ResponseEnvelope<JobDto> Warm(string deviceUri)
    {
        if (!TryDriver<ICameraDriver>(deviceUri, "camera", out var uri, out _, out var refused))
        {
            return refused.Value.As<JobDto>();
        }

        var name = NameOf(uri);
        return Start(WarmJob, uri, name, async (step, ct) =>
        {
            step.Report($"Warming {name}");
            await hub.WarmAndCoolerOffAsync(uri, timeProvider, logger, ct);
            return $"Warmed {name}; cooler off";
        });
    }

    /// <summary>Switches the cooler off at once, with no warm-up: what the Equipment tab's Force Off does to a cooler.</summary>
    public async Task<ResponseEnvelope<string>> CoolerOffAsync(string deviceUri, CancellationToken cancellationToken)
    {
        if (!TryIdle<ICameraDriver>(deviceUri, "camera", out var uri, out var camera, out var refused))
        {
            return refused.Value.As<string>();
        }
        var name = NameOf(uri);
        if (!camera.CanSetCoolerOn)
        {
            return ResponseEnvelope<string>.Fail($"{name} cannot switch its cooler");
        }

        await hub.CoolerOffAsync(uri, cancellationToken);
        return ResponseEnvelope<string>.Ok($"{name}: cooler off");
    }

    /// <summary>
    /// Changes the camera's gain, offset, binning and frame, each only when the request names it, and answers what the
    /// camera reads back. Every value is checked against what the camera takes BEFORE any is applied, so a bad one
    /// changes nothing.
    /// </summary>
    public async Task<ResponseEnvelope<CameraSettingsDto>> ApplySettingsAsync(CameraSettingsRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryIdle<ICameraDriver>(request.DeviceUri, "camera", out var uri, out var camera, out var refused))
        {
            return refused.Value.As<CameraSettingsDto>();
        }
        if (Invalid(camera, NameOf(uri), request) is { } invalid)
        {
            return ResponseEnvelope<CameraSettingsDto>.Fail(invalid);
        }

        if (request.Bin is not null || request.Frame is not null)
        {
            camera.SetFrame(request.Bin ?? camera.BinX, request.Frame is { } f ? new RoiRect(f.X, f.Y, f.Width, f.Height) : null);
        }
        if (request.Gain is { } gain)
        {
            await camera.SetGainAsync(gain, cancellationToken);
        }
        if (request.Offset is { } offset)
        {
            await camera.SetOffsetAsync(offset, cancellationToken);
        }

        return ResponseEnvelope<CameraSettingsDto>.Ok(new CameraSettingsDto
        {
            Gain = camera.UsesGainValue || camera.UsesGainMode ? await camera.GetGainAsync(cancellationToken) : null,
            Offset = camera.UsesOffsetValue || camera.UsesOffsetMode ? await camera.GetOffsetAsync(cancellationToken) : null,
            BinX = camera.BinX,
            BinY = camera.BinY,
            Frame = new FrameDto { X = camera.StartX, Y = camera.StartY, Width = camera.NumX, Height = camera.NumY },
        });
    }

    /// <summary>Why the camera cannot take the request as it stands, or null when it can.</summary>
    private static string? Invalid(ICameraDriver camera, string name, CameraSettingsRequestDto request)
    {
        if (request.Gain is { } gain)
        {
            if (camera.UsesGainValue && (gain < camera.GainMin || gain > camera.GainMax))
            {
                return $"{name} takes a gain between {camera.GainMin} and {camera.GainMax}";
            }
            if (camera.UsesGainMode && (gain < 0 || gain >= camera.Gains.Count))
            {
                return $"{name} takes a gain mode between 0 and {camera.Gains.Count - 1}";
            }
            if (!camera.UsesGainValue && !camera.UsesGainMode)
            {
                return $"{name} has no gain to set";
            }
        }
        if (request.Offset is { } offset)
        {
            if (camera.UsesOffsetValue && (offset < camera.OffsetMin || offset > camera.OffsetMax))
            {
                return $"{name} takes an offset between {camera.OffsetMin} and {camera.OffsetMax}";
            }
            if (camera.UsesOffsetMode && (offset < 0 || offset >= camera.Offsets.Count))
            {
                return $"{name} takes an offset mode between 0 and {camera.Offsets.Count - 1}";
            }
            if (!camera.UsesOffsetValue && !camera.UsesOffsetMode)
            {
                return $"{name} has no offset to set";
            }
        }
        var maxBin = Math.Min(camera.MaxBinX, camera.MaxBinY);
        if (request.Bin is { } bin && (bin < 1 || bin > maxBin))
        {
            return $"{name} bins between 1 and {maxBin}";
        }
        if (request.Frame is { } frame && (frame.X < 0 || frame.Y < 0 || frame.Width < 1 || frame.Height < 1))
        {
            return "A frame starts at or after the sensor's corner and is at least a pixel wide and high";
        }
        return null;
    }
}
