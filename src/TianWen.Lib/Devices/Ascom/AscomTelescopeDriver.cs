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

        CanSetTracking = _telescope.CanSetTracking;
        CanSetSideOfPier = _telescope.CanSetPierSide;
        CanPark = _telescope.CanPark;
        CanUnpark = _telescope.CanUnpark;
        CanSetPark = _telescope.CanSetPark;
        CanSlew = _telescope.CanSlew;
        CanSlewAsync = _telescope.CanSlewAsync;
        CanSync = _telescope.CanSync;
        CanPulseGuide = _telescope.CanPulseGuide;
        CanSetRightAscensionRate = _telescope.CanSetRightAscensionRate;
        CanSetDeclinationRate = _telescope.CanSetDeclinationRate;
        CanSetGuideRates = _telescope.CanSetGuideRates;

        return ValueTask.FromResult(true);
    }

    public ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken = default)
    {
        if (CanSlewAsync)
        {
            _telescope.SlewToCoordinatesAsync(ra, dec);
            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlewAsync)} connected={Connected} initialized={_telescope is not null}");
        }
    }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => _trackingSpeeds;

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return ValueTask.FromResult((TrackingSpeed)_telescope.TrackingRate);
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(GetTrackingSpeedAsync)} connected={Connected}");
        }
    }

    public ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        _telescope.TrackingRate = (int)value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.AtHome);

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.AtPark);

    public ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.Slewing);

    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.SiderealTime);

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return ConditionHA(lst - ra);
    }

    public bool TimeIsSetByUs { get; private set; }

    public ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult(Connected ? _telescope.UTCDate : null as DateTime?);
        }
        catch
        {
            return default;
        }
    }

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
        => ValueTask.FromResult(_telescope.Tracking);

    public ValueTask SetTrackingAsync(bool value, CancellationToken cancellationToken)
    {
        if (Connected)
        {
            if (!CanSetTracking)
            {
                throw new InvalidOperationException("Driver does not support setting tracking");
            }
            _telescope.Tracking = value;

            return ValueTask.CompletedTask;
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
        => ValueTask.FromResult((PointingState)_telescope.SideOfPier);

    public ValueTask SetSideOfPierAsync(PointingState value, CancellationToken cancellationToken)
    {
        if (CanSetSideOfPier)
        {
            _telescope.SideOfPier = (int)value;
            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException("Cannot set side of pier to: " + value);
        }
    }

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
        => ValueTask.FromResult((PointingState)_telescope.DestinationSideOfPier(ra, dec));

    public EquatorialCoordinateType EquatorialSystem => (EquatorialCoordinateType)_telescope.EquatorialSystem;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken) => ValueTask.FromResult((AlignmentMode)_telescope.AlignmentMode);

    public ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.RightAscension);

    public ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.Declination);

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.SiteElevation);

    public ValueTask SetSiteElevationAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.SiteElevation = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.SiteLatitude);

    public ValueTask SetSiteLatitudeAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.SiteLatitude = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.SiteLongitude);

    public ValueTask SetSiteLongitudeAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.SiteLongitude = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_telescope.IsPulseGuiding);

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_telescope.RightAscensionRate);

    public ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.RightAscensionRate = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_telescope.DeclinationRate);

    public ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.DeclinationRate = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_telescope.GuideRateRightAscension);

    public ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.GuideRateRightAscension = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_telescope.GuideRateDeclination);

    public ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
    {
        _telescope.GuideRateDeclination = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        if (Connected && CanPark)
        {
            _telescope.Park();
            return ValueTask.CompletedTask;
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
            _telescope.Unpark();
            return ValueTask.CompletedTask;
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
            _telescope.PulseGuide((int)direction, (int)duration.TotalMilliseconds);
            return ValueTask.CompletedTask;
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
            _telescope.SyncToCoordinates(ra, dec);
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
            _telescope.AbortSlew();
            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlewAsync)} connected={Connected}");
        }
    }

    public bool CanMoveAxis(TelescopeAxis axis) => _telescope.CanMoveAxis((int)axis);

    // TODO: implement axis rates
    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis)
    {
        throw new NotImplementedException();
    }

    public ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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
