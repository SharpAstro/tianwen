using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// What to shoot: one row of a dark library.
/// </summary>
/// <param name="Exposure">Exposure per frame. A dark must match the LIGHT's exposure exactly, since
/// dark current accumulates with time, so this is never rounded or adjusted.</param>
/// <param name="Count">Frames to capture.</param>
/// <param name="Gain">Camera gain, or null to leave whatever is set. Matters more than it looks on a
/// body whose conversion gain changes at a threshold: a Uranus-C at gain 200 is in low conversion
/// gain and at 220 is in high, and no header says which, so a dark shot on the wrong side of it does
/// not describe the light however well the temperature matches.</param>
/// <param name="Offset">Camera offset / black level, or null to leave it. A dark with a different
/// offset has a different pedestal and subtracts to the wrong level.</param>
/// <param name="Bin">Binning, 1 unless the lights were binned.</param>
/// <param name="FrameType">
/// <see cref="TianWen.Lib.Imaging.FrameType.Dark"/> or
/// <see cref="TianWen.Lib.Imaging.FrameType.Bias"/>.
/// <para>A bias IS a dark of the shortest exposure the camera accepts, but it must be LABELLED as
/// one: <see cref="TianWen.Lib.Imaging.Calibration.MasterGroupKey"/> matches on the frame type, so a
/// bias written as a Dark is a frame the calibration resolver will never find when it wants a bias,
/// however short its exposure. The type is the fact; the exposure is only how it was achieved.</para>
/// </param>
public sealed record DarkFrameRunOptions(
    TimeSpan Exposure,
    int Count,
    short? Gain = null,
    int? Offset = null,
    int Bin = 1,
    FrameType FrameType = FrameType.Dark);

/// <summary>One captured frame and the state it was captured in.</summary>
public sealed record DarkFrameCaptured(string Path, DateTimeOffset StartedUtc, double SensorTemperatureC);

/// <summary>
/// Captures a set of dark frames from one camera, with no mount, no cover and no filter wheel.
/// </summary>
/// <remarks>
/// <para>This lives in the library rather than in the CLI verb that drives it, for the same reason
/// the flat run does: a capture is product logic, and a copy of it in a script diverges from the one
/// the session uses without anything failing.</para>
/// <para><b>Nothing here darkens the sensor.</b> <see cref="FrameType.Dark"/> asks the driver for a
/// closed mechanical shutter where the body has one, and most CMOS astro cameras do not, so on those
/// the operator caps the telescope. The frame type is still the thing that matters, because it is
/// what the stacker matches on; the path is cosmetic.</para>
/// <para><b>Every frame carries its OWN measured temperature</b>, stamped by the driver into
/// <c>CCD-TEMP</c> at capture. That is not a convenience on an unregulated body: there is no setpoint
/// to record instead, and the sensor follows the room, so the per-frame value is the only true
/// statement about what was shot. The run reports the observed spread so a caller can see whether the
/// set is one library row or several.</para>
/// </remarks>
public sealed class DarkFrameRun(IExternal external, ITimeProvider timeProvider, ILogger<DarkFrameRun> logger)
{
    /// <summary>
    /// How often the frame-ready flag is polled. Short enough not to add meaningfully to a long
    /// exposure's wall clock, long enough not to spin.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async ValueTask<IReadOnlyList<DarkFrameCaptured>> RunAsync(
        ICameraDriver camera,
        DarkFrameRunOptions options,
        IProgress<DarkFrameCaptured>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Exposure, TimeSpan.Zero);

        if (options.Bin is > 0 and var bin && bin != camera.BinX)
        {
            camera.BinX = bin;
            camera.BinY = bin;
        }

        if (options.Gain is { } gain)
        {
            await camera.SetGainAsync(gain, cancellationToken);
        }

        if (options.Offset is { } offset)
        {
            await camera.SetOffsetAsync(offset, cancellationToken);
        }

        // READ BACK what the camera accepted, and refuse if it is not what was asked for.
        //
        // A calibration frame is defined ENTIRELY by the state it was captured in, so a silently
        // ignored setting does not produce a worse frame, it produces a frame that belongs to a
        // different library row and will never match the lights it was shot for. Both driver
        // setters here fail SILENTLY by design (an out-of-range bin is a no-op, not a throw), which
        // is survivable for a preview and not for this. Found the hard way: a bin-2 run was asked
        // for, the driver kept bin 1, and 50 frames would have been written that no light can use.
        if (camera.BinX != options.Bin || camera.BinY != options.Bin)
        {
            throw new InvalidOperationException(
                $"Requested bin {options.Bin} but the camera reports {camera.BinX}x{camera.BinY} "
                + $"(max bin {camera.MaxBinX}). Refusing to capture frames that would not match.");
        }

        if (options.Gain is { } wantGain && await camera.GetGainAsync(cancellationToken) is var gotGain && gotGain != wantGain)
        {
            throw new InvalidOperationException($"Requested gain {wantGain} but the camera reports {gotGain}.");
        }

        if (options.Offset is { } wantOffset && await camera.GetOffsetAsync(cancellationToken) is var gotOffset && gotOffset != wantOffset)
        {
            throw new InvalidOperationException($"Requested offset {wantOffset} but the camera reports {gotOffset}.");
        }

        logger.LogInformation(
            "Capturing {Count} {FrameType} at bin {Bin}, ROI {Width}x{Height}, gain {Gain}, offset {Offset}",
            options.Count, options.FrameType, camera.BinX, camera.NumX, camera.NumY,
            await camera.GetGainAsync(cancellationToken), await camera.GetOffsetAsync(cancellationToken));

        var folder = Path.Combine(
            external.ImageOutputFolder.FullName,
            options.FrameType is FrameType.Bias ? "Bias" : "Darks",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo));
        Directory.CreateDirectory(folder);

        var captured = new List<DarkFrameCaptured>(options.Count);

        for (var frame = 1; frame <= options.Count; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedUtc = await camera.StartExposureAsync(options.Exposure, options.FrameType, cancellationToken);

            while (!await camera.GetImageReadyAsync(cancellationToken))
            {
                await timeProvider.SleepAsync(PollInterval, cancellationToken);
            }

            var image = await camera.GetImageAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Camera reported frame {frame} ready but returned no image");

            try
            {
                var meta = image.ImageMeta;

                // The state goes in the NAME as well as the header, because a dark library is read by
                // people as well as by the stacker, and a folder of frame_0001.fits tells a human
                // nothing about which row they belong to.
                var kind = options.FrameType is FrameType.Bias ? "bias" : "dark";

                // Binning is named only when it is not 1. A calibration frame is defined by the
                // state it was shot in, and binning changes the GEOMETRY, so two sets that differ
                // only by it are not interchangeable and must not look alike in a folder listing.
                // Omitting bin 1 keeps the common case short, which is the same reason a filter is
                // not named on a dark.
                var binTag = meta.BinX > 1 ? $"_bin{meta.BinX}" : "";

                var stem = string.Create(CultureInfo.InvariantCulture,
                    $"{kind}_{options.Exposure.TotalSeconds:0.#####}s_g{meta.Gain}_o{meta.Offset}{binTag}_{meta.CCDTemperature:0.0}C_{startedUtc:yyyy-MM-ddTHH_mm_ss}_{frame:0000}");
                var path = Path.Combine(folder, external.GetSafeFileName(stem) + ".fits");

                await external.WriteFitsFileAsync(image, path);

                var record = new DarkFrameCaptured(path, startedUtc, meta.CCDTemperature);
                captured.Add(record);
                progress?.Report(record);

                logger.LogInformation(
                    "{Kind} {Frame}/{Count}: {Exposure}s at {Temp:0.0} C, gain {Gain}, offset {Offset} -> {Path}",
                    options.FrameType, frame, options.Count, options.Exposure.TotalSeconds, meta.CCDTemperature, meta.Gain, meta.Offset, path);
            }
            finally
            {
                image.Release();
            }
        }

        return captured;
    }
}
