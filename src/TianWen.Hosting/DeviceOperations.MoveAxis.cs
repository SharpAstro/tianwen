using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// A mount's axis moved at a rate, the device plane's fifth part (P2 of docs/plans/hardware-in-the-server.md, #929):
/// LEASED, so it stops by itself <see cref="NodeWire.MoveAxisLease"/> after the last request that asked for it. A
/// move-axis used to run until something stopped it, so a client that died, or lost its connection, mid-move left the
/// axis running.
/// </summary>
/// <remarks>
/// The motion is one job on the mount (<see cref="MoveAxisJob"/>), holding both axes. A request while it runs RENEWS it,
/// and changes the axis it names to its rate; the job stops both axes when the lease lapses, when both are asked to
/// stop, or when it is cancelled (<c>POST /devices/mount/stop</c>, <c>DELETE /jobs/{id}</c>). A renewal racing the
/// lapse can land just as the job stops; the next renewal starts a new job, a hiccup rather than a runaway.
/// </remarks>
internal sealed partial class DeviceOperations
{
    internal const string MoveAxisJob = "move-axis";

    /// <summary>The two axes a move-axis moves; an array, since a span cannot be walked across an await.</summary>
    private static readonly TelescopeAxis[] Axes = [TelescopeAxis.Primary, TelescopeAxis.Seconary];

    /// <summary>How often a move-axis job looks at its lease.</summary>
    private static readonly TimeSpan MoveAxisTick = TimeSpan.FromMilliseconds(100);

    // The motion each move-axis job carries out, by device key: what requests renew and change while the job runs.
    private readonly ConcurrentDictionary<string, AxisMotion> _axisMotions = new ConcurrentDictionary<string, AxisMotion>(StringComparer.Ordinal);

    /// <summary>Both axes' rates and when a client last asked for the motion; written by requests, read by the job.</summary>
    private sealed class AxisMotion(long renewedAt)
    {
        private double _primary;
        private double _secondary;
        private long _renewedAt = renewedAt;

        public double Rate(TelescopeAxis axis) => axis is TelescopeAxis.Primary ? Volatile.Read(ref _primary) : Volatile.Read(ref _secondary);

        public void Set(TelescopeAxis axis, double rate)
        {
            if (axis is TelescopeAxis.Primary)
            {
                Volatile.Write(ref _primary, rate);
            }
            else
            {
                Volatile.Write(ref _secondary, rate);
            }
        }

        public bool Moving => Rate(TelescopeAxis.Primary) != 0 || Rate(TelescopeAxis.Seconary) != 0;

        public long RenewedAt => Volatile.Read(ref _renewedAt);

        public void Renew(long now) => Volatile.Write(ref _renewedAt, now);
    }

    /// <summary>
    /// Moves the axis at the rate, or stops it at 0, and renews the lease on the mount's motion. Starts the motion's job
    /// when none runs; renews and changes the one that does; refuses while another kind of job holds the mount.
    /// </summary>
    public async Task<ResponseEnvelope<JobDto>> MoveAxisAsync(MoveAxisRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryDriver<IMountDriver>(request.DeviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        var (axis, rate) = (request.Axis, request.Rate);
        if (axis is not (TelescopeAxis.Primary or TelescopeAxis.Seconary) || !mount.CanMoveAxis(axis))
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} cannot move its {axis} axis");
        }
        if (rate != 0 && !InRange(mount.AxisRates(axis), Math.Abs(rate)))
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} does not move its {axis} axis at {Math.Abs(rate)} degrees a second");
        }

        var now = timeProvider.GetTimestamp();
        if (jobs.TryGetRunningOn(uri, out var running))
        {
            if (running.Kind is not MoveAxisJob || !_axisMotions.TryGetValue(uri.DeviceKey, out var held))
            {
                return Busy(name, running).As<JobDto>();
            }

            held.Renew(now);
            if (held.Rate(axis) != rate)
            {
                await mount.MoveAxisAsync(axis, rate, cancellationToken);
                held.Set(axis, rate);
            }
            return ResponseEnvelope<JobDto>.Accepted(running);
        }
        if (rate == 0)
        {
            return ResponseEnvelope<JobDto>.NotFound($"{name} is not moving an axis");
        }

        var motion = new AxisMotion(now);
        motion.Set(axis, rate);
        _axisMotions[uri.DeviceKey] = motion;
        return Start(MoveAxisJob, uri, name, async (step, ct) =>
        {
            step.Report($"Moving {name}'s axes");
            var lapsed = false;
            try
            {
                // The rates as they stand now: a renewal between the request and this may already have changed one.
                foreach (var moving in Axes)
                {
                    if (motion.Rate(moving) is var axisRate && axisRate != 0)
                    {
                        await mount.MoveAxisAsync(moving, axisRate, ct);
                    }
                }

                while (motion.Moving)
                {
                    if (timeProvider.GetElapsedTime(motion.RenewedAt) >= NodeWire.MoveAxisLease)
                    {
                        lapsed = true;
                        break;
                    }
                    await timeProvider.SleepAsync(MoveAxisTick, ct);
                }
            }
            finally
            {
                _axisMotions.TryRemove(new KeyValuePair<string, AxisMotion>(uri.DeviceKey, motion));
                await StopAxesAsync(mount, uri);
            }
            return lapsed ? $"{name} stopped: nothing renewed its move within {NodeWire.MoveAxisLease.TotalSeconds:F0} s" : $"{name} stopped";
        });
    }

    /// <summary>
    /// Stops both axes, whatever ended the motion: on the node's own token, never the job's, since a stop that a
    /// cancellation could skip is how an axis keeps running.
    /// </summary>
    private async Task StopAxesAsync(IMountDriver mount, Uri uri)
    {
        foreach (var axis in Axes)
        {
            if (!mount.CanMoveAxis(axis))
            {
                continue;
            }
            try
            {
                await mount.MoveAxisAsync(axis, 0, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not stop the {Axis} axis of {Mount} after a move-axis", axis, uri);
            }
        }
    }

    private static bool InRange(IReadOnlyList<AxisRate> rates, double rate)
    {
        foreach (var range in rates)
        {
            if (rate >= range.Mininum && rate <= range.Maximum)
            {
                return true;
            }
        }
        return false;
    }
}
