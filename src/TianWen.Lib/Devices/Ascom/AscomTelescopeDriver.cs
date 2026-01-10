using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using AscomGuideDirection = ASCOM.Common.DeviceInterfaces.GuideDirection;
using AscomTelescope = ASCOM.Com.DriverAccess.Telescope;
using AscomTelescopeAxis = ASCOM.Common.DeviceInterfaces.TelescopeAxis;
using AscomTrackingSpeed = ASCOM.Common.DeviceInterfaces.DriveRate;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Ascom;

internal class AscomTelescopeDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomTelescope>(device, external, (progId, logger) => new AscomTelescope(progId, new AscomLoggerWrapper(logger))), IMountDriver
{
    private List<TrackingSpeed> _trackingSpeeds = [];

    private void AscomTelescopeDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected )
        {
            var trackingRates = _comObject.TrackingRates;

            var trackingSpeeds = new List<TrackingSpeed>(trackingRates.Count);
            foreach (var trackingRate in trackingRates)
            {
                if (trackingRate is AscomTrackingSpeed ascomValue)
                {
                    trackingSpeeds.Add((TrackingSpeed)ascomValue);
                }
            }
            Interlocked.Exchange(ref _trackingSpeeds, trackingSpeeds);

            CanSetTracking = _comObject.CanSetTracking is bool canSetTracking && canSetTracking;
            CanSetSideOfPier = _comObject.CanSetPierSide is bool canSetSideOfPier && canSetSideOfPier;
            CanPark = _comObject.CanPark is bool canPark && canPark;
            CanUnpark = _comObject.CanUnpark is bool canUnpark && canUnpark;
            CanSetPark = _comObject.CanSetPark is bool canSetPark && canSetPark;
            CanSlew = _comObject.CanSlew is bool canSlew && canSlew;
            CanSlewAsync = _comObject.CanSlewAsync is bool canSlewAsync && canSlewAsync;
            CanSync = _comObject.CanSync is bool canSync && canSync;
            CanPulseGuide = _comObject.CanPulseGuide is bool canPulseGuide && canPulseGuide;
            CanSetRightAscensionRate = _comObject.CanSetRightAscensionRate is bool canSetRightAscensionRate && canSetRightAscensionRate;
            CanSetDeclinationRate = _comObject.CanSetDeclinationRate is bool canSetDeclinationRate && canSetDeclinationRate;
            CanSetGuideRates = _comObject.CanSetGuideRates is bool canSetGuideRates && canSetGuideRates;
        }
    }

    public ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken = default)
    {
        if (_comObject.CanSlewAsync is bool canSlewAsync && canSlewAsync)
        {
            _comObject.SlewToCoordinatesAsync(ra, dec);

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlewAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => _trackingSpeeds;

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return ValueTask.FromResult((TrackingSpeed)_comObject.TrackingRate);
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(GetTrackingSpeedAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    } 
    
    public ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        _comObject.TrackingRate = (AscomTrackingSpeed)value;

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.AtHome is bool atHome && atHome);

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.AtPark is bool atPark && atPark);

    public ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.Slewing is bool slewing && slewing);
    
    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.SiderealTime is double siderealTime ? siderealTime : throw new InvalidOperationException($"Failed to retrieve sidereal time from device connected={Connected} initialized={_comObject is not null}"));

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
            return ValueTask.FromResult(Connected && _comObject.UTCDate is DateTime utcDate ? utcDate : null as DateTime?);
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
            _comObject.UTCDate = value;
            TimeIsSetByUs = true;
        }
        catch
        {
            TimeIsSetByUs = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_comObject.Tracking is bool tracking && tracking);

    public ValueTask SetTrackingAsync(bool value, CancellationToken cancellationToken)
    {
        if (Connected)
        {
            if (_comObject.CanSetTracking is false)
            {
                throw new InvalidOperationException("Driver does not support setting tracking");
            }
            _comObject.Tracking = value;

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to set tracking to {value} connected={Connected} initialized={_comObject is not null}");
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
        => ValueTask.FromResult((PointingState)_comObject.SideOfPier);


    public ValueTask SetSideOfPierAsync(PointingState value, CancellationToken cancellationToken)
    {
        if (CanSetSideOfPier)
        {
            _comObject.SideOfPier = (ASCOM.Common.DeviceInterfaces.PointingState)value;

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException("Cannot set side of pier to: " + value);
        }
    }

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
        => ValueTask.FromResult((PointingState)_comObject.DestinationSideOfPier(ra, dec));

    public EquatorialCoordinateType EquatorialSystem => (EquatorialCoordinateType)_comObject.EquatorialSystem;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken) => ValueTask.FromResult((AlignmentMode)_comObject.AlignmentMode);

    public ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.RightAscension);

    public ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.Declination);

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.SiteElevation);
    
    public ValueTask SetSiteElevationAsync(double value, CancellationToken cancellationToken)
    {
        _comObject.SiteElevation = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.SiteLatitude);

    public ValueTask SetSiteLatitudeAsync(double value, CancellationToken cancellationToken)
    {
        _comObject.SiteLatitude = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.SiteLongitude);

    public ValueTask SetSiteLongitudeAsync(double value, CancellationToken cancellationToken)
    {
        _comObject.SiteLongitude = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_comObject.IsPulseGuiding);



    public double RightAscensionRate
    {
        get => _comObject.RightAscensionRate;
        set => _comObject.RightAscensionRate = value;
    }

    public double DeclinationRate
    {
        get => _comObject.DeclinationRate;
        set => _comObject.DeclinationRate = value;
    }

    public double GuideRateRightAscension
    {
        get => _comObject.GuideRateRightAscension;
        set => _comObject.GuideRateRightAscension = value;
    }

    public double GuideRateDeclination
    {
        get => _comObject.GuideRateDeclination;
        set => _comObject.GuideRateDeclination = value;
    }

    public ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        if (Connected && CanPark )
        {
            _comObject.Park();

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(ParkAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    }
    public ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        if (Connected && CanUnpark )
        {
            _comObject.Unpark();

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(UnparkAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (Connected && CanPulseGuide )
        {
            _comObject.PulseGuide((AscomGuideDirection)direction, (int)duration.TotalMilliseconds);

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(PulseGuideAsync)} connected={Connected} initialized={_comObject is not null}");
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
            _comObject.SyncToCoordinates(ra, dec);
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(SyncRaDecAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        if (Connected )
        {
            _comObject.AbortSlew();

            return ValueTask.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlewAsync)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public bool CanMoveAxis(TelescopeAxis axis) => _comObject.CanMoveAxis((AscomTelescopeAxis)axis);

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