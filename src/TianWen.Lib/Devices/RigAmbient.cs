using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Devices;

/// <summary>
/// The rig's own ambient temperature for ONE camera, read by whoever drives the camera and handed to its warm-up
/// (<see cref="CameraReading.RigAmbientC"/>), never driver to driver. An ordered list of sources, most trusted first: the
/// first plausible reading wins, and a source that throws or reads nothing is skipped. A later source (a power box's
/// ambient channel, once a profile holds power boxes) is one more entry in the list.
/// </summary>
public static class RigAmbient
{
    /// <summary>
    /// The first plausible reading of <paramref name="sources"/>, in order, or NaN when none gives one. A read is
    /// best-effort: a source that throws (other than for cancellation) counts as no reading.
    /// </summary>
    public static async ValueTask<double> FirstPlausibleAsync(IEnumerable<Func<CancellationToken, ValueTask<double>>> sources, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            double reading;
            try
            {
                reading = await source(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (CameraReading.IsPlausibleTemperatureC(reading))
            {
                return reading;
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// The rig ambient for the camera at <paramref name="cameraUri"/> outside a session, which has no single active profile: read
    /// from the first discovered profile whose OTA names this camera (by device key, as <c>MountLimitWatcher</c> finds a mount's
    /// profile), from that OTA's connected focuser, then the profile's connected weather device. NaN when the camera is in no
    /// profile, no source is connected, or <paramref name="discovery"/> is null. With <paramref name="skipLeased"/> a source a
    /// run holds is not read (a reader outside the run must not touch a device the run reads), and counts as no reading.
    /// </summary>
    public static ValueTask<double> ForCameraAsync(IDeviceHub hub, IDeviceDiscovery? discovery, Uri cameraUri, CancellationToken cancellationToken, bool skipLeased = false)
    {
        if (discovery is null)
        {
            return ValueTask.FromResult(double.NaN);
        }

        foreach (var device in discovery.RegisteredDevices(DeviceType.Profile))
        {
            if (device is not Profile { Data: { } data })
            {
                continue;
            }

            foreach (var ota in data.OTAs)
            {
                if (!DeviceUriExtensions.DeviceKeyComparer.Equals(ota.Camera.DeviceKey, cameraUri.DeviceKey))
                {
                    continue;
                }

                var sources = new List<Func<CancellationToken, ValueTask<double>>>(2);
                if (ota.Focuser is { } focuserUri && !(skipLeased && hub.TryGetLease(focuserUri, out _)) && hub.TryGetConnectedDriver<IFocuserDriver>(focuserUri, out var focuser))
                {
                    sources.Add(ct => focuser.GetTemperatureAsync(ct));
                }

                if (data.Weather is { } weatherUri && !(skipLeased && hub.TryGetLease(weatherUri, out _)) && hub.TryGetConnectedDriver<IWeatherDriver>(weatherUri, out var weather))
                {
                    sources.Add(_ => ValueTask.FromResult(weather.Temperature));
                }

                return FirstPlausibleAsync(sources, cancellationToken);
            }
        }

        return ValueTask.FromResult(double.NaN);
    }
}
