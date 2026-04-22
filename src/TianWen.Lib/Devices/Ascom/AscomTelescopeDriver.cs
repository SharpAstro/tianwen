using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices.Ascom.ComInterop;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal class AscomTelescopeDriver : AscomDeviceDriverBase, IMountDriver
{
    private readonly AscomDispatchTelescope _telescope;
    private List<TrackingSpeed> _trackingSpeeds = [];

    internal AscomTelescopeDriver(AscomDevice device, IServiceProvider sp) : base(device, sp)
    {
        _telescope = new AscomDispatchTelescope(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        // Query tracking rates via IDispatch (TrackingRates returns a collection)
        // For now we support the standard rates
        _trackingSpeeds = [TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar, TrackingSpeed.King];

        CanSetTracking = SafeGet(() => _telescope.CanSetTracking, false);
        CanSetSideOfPier = SafeGet(() => _telescope.CanSetPierSide, false);
        CanPark = SafeGet(() => _telescope.CanPark, false);
        CanUnpark = SafeGet(() => _telescope.CanUnpark, false);
        CanSetPark = SafeGet(() => _telescope.CanSetPark, false);
        CanSlew = SafeGet(() => _telescope.CanSlew, false);
        CanSlewAsync = SafeGet(() => _telescope.CanSlewAsync, false);
        CanSync = SafeGet(() => _telescope.CanSync, false);
        CanPulseGuide = SafeGet(() => _telescope.CanPulseGuide, false);
        CanSetRightAscensionRate = SafeGet(() => _telescope.CanSetRightAscensionRate, false);
        CanSetDeclinationRate = SafeGet(() => _telescope.CanSetDeclinationRate, false);
        CanSetGuideRates = SafeGet(() => _telescope.CanSetGuideRates, false);

        EquatorialSystem = SafeGet(() => (EquatorialCoordinateType)_telescope.EquatorialSystem, EquatorialCoordinateType.Topocentric);

        return ValueTask.FromResult(true);
    }

    public ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken = default)
    {
        if (CanSlewAsync)
        {
            return SafeValueTask(() => _telescope.SlewToCoordinatesAsync(ra, dec));
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(BeginSlewRaDecAsync)} connected={Connected} initialized={_telescope is not null}");
        }
    }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => _trackingSpeeds;

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return ValueTask.FromResult(SafeGet(() => (TrackingSpeed)_telescope.TrackingRate, TrackingSpeed.Sidereal));
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(GetTrackingSpeedAsync)} connected={Connected}");
        }
    }

    public ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.TrackingRate = (int)value);

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.AtHome, false));

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.AtPark, false));

    public ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.Slewing, false));

    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.SiderealTime, double.NaN));

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return ConditionHA(lst - ra);
    }

    public bool TimeIsSetByUs { get; private set; }

    public ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Connected ? SafeGet(() => (DateTime?)_telescope.UTCDate, null) : null);

    public ValueTask SetUTCDateAsync(DateTime value, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Mount is not connected");
        }
        try
        {
            _telescope.UTCDate = value;
            TimeIsSetByUs = true;
        }
        catch
        {
            TimeIsSetByUs = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.Tracking, false));

    public ValueTask SetTrackingAsync(bool value, CancellationToken cancellationToken)
    {
        if (Connected)
        {
            if (!CanSetTracking)
            {
                throw new InvalidOperationException("Driver does not support setting tracking");
            }
            return SafeValueTask(() => _telescope.Tracking = value);
        }
        else
        {
            throw new InvalidOperationException($"Failed to set tracking to {value} connected={Connected}");
        }
    }

    public bool CanSetTracking { get; private set; }

    public bool CanSetSideOfPier { get; private set; }

    public bool CanPark { get; private set; }

    public bool CanUnpark { get; private set; }

    public bool CanSetPark { get; private set; }

    public bool CanSlew { get; private set; }

    public bool CanSlewAsync { get; private set; }

    public bool CanSync { get; private set; }

    public bool CanPulseGuide { get; private set; }

    public bool CanSetRightAscensionRate { get; private set; }

    public bool CanSetDeclinationRate { get; private set; }

    public bool CanSetGuideRates { get; private set; }

    public ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => (PointingState)_telescope.SideOfPier, PointingState.Unknown));

    public ValueTask SetSideOfPierAsync(PointingState value, CancellationToken cancellationToken)
    {
        if (CanSetSideOfPier)
        {
            return SafeValueTask(() => _telescope.SideOfPier = (int)value);
        }
        else
        {
            throw new InvalidOperationException("Cannot set side of pier to: " + value);
        }
    }

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => (PointingState)_telescope.DestinationSideOfPier(ra, dec), PointingState.Unknown));

    public EquatorialCoordinateType EquatorialSystem { get; private set; } = EquatorialCoordinateType.Topocentric;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => (AlignmentMode)_telescope.AlignmentMode, AlignmentMode.GermanPolar));

    public ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.RightAscension, double.NaN));

    public ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.Declination, double.NaN));

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.SiteElevation, double.NaN));

    public ValueTask SetSiteElevationAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.SiteElevation = value);

    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.SiteLatitude, double.NaN));

    public ValueTask SetSiteLatitudeAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.SiteLatitude = value);

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.SiteLongitude, double.NaN));

    public ValueTask SetSiteLongitudeAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.SiteLongitude = value);

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.IsPulseGuiding, false));

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.RightAscensionRate, 0.0));

    public ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.RightAscensionRate = value);

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.DeclinationRate, 0.0));

    public ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.DeclinationRate = value);

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.GuideRateRightAscension, double.NaN));

    public ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.GuideRateRightAscension = value);

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SafeGet(() => _telescope.GuideRateDeclination, double.NaN));

    public ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
        => SafeValueTask(() => _telescope.GuideRateDeclination = value);

    public ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        if (Connected && CanPark)
        {
            return SafeValueTask(() => _telescope.Park());
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(ParkAsync)} connected={Connected}");
        }
    }

    public ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        if (Connected && CanUnpark)
        {
            return SafeValueTask(() => _telescope.Unpark());
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(UnparkAsync)} connected={Connected}");
        }
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (Connected && CanPulseGuide)
        {
            return SafeValueTask(() => _telescope.PulseGuide((int)direction, (int)duration.TotalMilliseconds));
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(PulseGuideAsync)} connected={Connected}");
        }
    }

    public async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        // prevent syncs on other side of meridian (most mounts do not support that).
        if (Connected
            && CanSync
            && await IsTrackingAsync(cancellationToken)
            && !await AtParkAsync(cancellationToken)
            && await DestinationSideOfPierAsync(ra, dec, cancellationToken) == await GetSideOfPierAsync(cancellationToken))
        {
            SafeDo(() => _telescope.SyncToCoordinates(ra, dec));
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(SyncRaDecAsync)} connected={Connected}");
        }
    }

    public ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return SafeValueTask(() => _telescope.AbortSlew());
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlewAsync)} connected={Connected}");
        }
    }

    public bool CanMoveAxis(TelescopeAxis axis) => SafeGet(() => _telescope.CanMoveAxis((int)axis), false);

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis)
        => SafeGet(() => ReadAxisRates(axis), Array.Empty<AxisRate>());

    private IReadOnlyList<AxisRate> ReadAxisRates(TelescopeAxis axis)
    {
        // ASCOM ITelescope.AxisRates(axis) returns an IAxisRates collection:
        //   Count                 — int
        //   Item(i)  (1-indexed)  — IRate { Minimum, Maximum }
        using var rates = _telescope.AxisRates((int)axis);
        var count = rates.GetInt("Count");
        if (count <= 0)
        {
            return [];
        }

        var result = new AxisRate[count];
        for (int i = 1; i <= count; i++)
        {
            using var rate = rates.GetPropertyDispatch("Item", i);
            result[i - 1] = new AxisRate(rate.GetDouble("Minimum"), rate.GetDouble("Maximum"));
        }
        return result;
    }

    public ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException($"Failed to execute {nameof(MoveAxisAsync)} connected={Connected}");
        }
        // Rate == 0 means "stop the axis" and is always permitted; any non-zero rate requires CanMoveAxis.
        if (rate != 0.0 && !CanMoveAxis(axis))
        {
            throw new InvalidOperationException($"Driver does not support MoveAxis on {axis}");
        }
        return SafeValueTask(() => _telescope.MoveAxis((int)axis, rate));
    }

    public ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
