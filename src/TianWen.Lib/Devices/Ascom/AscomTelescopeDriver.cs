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

    public Task BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken = default)
    {
        if (_comObject.CanSlewAsync is bool canSlewAsync && canSlewAsync)
        {
            _comObject.SlewToCoordinatesAsync(ra, dec);

            return Task.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlew)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => _trackingSpeeds;

    public TrackingSpeed TrackingSpeed
    {
        get => (TrackingSpeed)_comObject.TrackingRate;
        set => _comObject.TrackingRate = (AscomTrackingSpeed)value;
    }

    public bool AtHome => _comObject.AtHome is bool atHome && atHome;

    public bool AtPark => _comObject.AtPark is bool atPark && atPark;

    public bool IsSlewing => _comObject.Slewing is bool slewing && slewing;

    public double SiderealTime => _comObject.SiderealTime is double siderealTime ? siderealTime : throw new InvalidOperationException($"Failed to retrieve {nameof(SiderealTime)} from device connected={Connected} initialized={_comObject is not null}");

    public bool TimeIsSetByUs { get; private set; }

    public DateTime? UTCDate
    {
        get
        {
            try
            {
                return Connected && _comObject.UTCDate is DateTime utcDate ? utcDate : default;
            }
            catch
            {
                return default;
            }
        }

        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Mount is not connected");
            }
            else if (value is { } utcDate)
            {
                try
                {
                    _comObject.UTCDate = utcDate;
                    TimeIsSetByUs = true;
                }
                catch
                {
                    TimeIsSetByUs = false;
                }
            }
            else
            {
                TimeIsSetByUs = false;
            }
        }
    }

    public bool Tracking
    {
        get => _comObject.Tracking is bool tracking && tracking;
        set
        {
            if (Connected )
            {
                if (_comObject.CanSetTracking is false)
                {
                    throw new InvalidOperationException("Driver does not support setting tracking");
                }
                _comObject.Tracking = value;
            }
            else
            {
                throw new InvalidOperationException($"Failed to set {nameof(Tracking)} to {value} connected={Connected} initialized={_comObject is not null}");
            }
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

    public PointingState SideOfPier
    {
        get => (PointingState)_comObject.SideOfPier;
        set
        {
            if (CanSetSideOfPier)
            {
                _comObject.SideOfPier = (ASCOM.Common.DeviceInterfaces.PointingState)value;
            }
            else
            {
                throw new InvalidOperationException("Cannot set side of pier to: " + value);
            }
        }
    }

    public PointingState DestinationSideOfPier(double ra, double dec) => (PointingState)_comObject.DestinationSideOfPier(ra, dec);

    public EquatorialCoordinateType EquatorialSystem => (EquatorialCoordinateType)_comObject.EquatorialSystem;

    public AlignmentMode Alignment => (AlignmentMode)_comObject.AlignmentMode;

    public double RightAscension => _comObject.RightAscension;

    public double Declination => _comObject.Declination;

    public double SiteElevation
    {
        get => _comObject.SiteElevation;
        set => _comObject.SiteElevation = value;
    }

    public double SiteLatitude
    {
        get => _comObject.SiteLatitude;
        set => _comObject.SiteLatitude = value;
    }

    public double SiteLongitude
    {
        get => _comObject.SiteLongitude;
        set => _comObject.SiteLongitude = value;
    }

    public bool IsPulseGuiding => _comObject.IsPulseGuiding;

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

    public void Park()
    {
        if (Connected && CanPark )
        {
            _comObject.Park();
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(Park)} connected={Connected} initialized={_comObject is not null}");
        }
    }
    public void Unpark()
    {
        if (Connected && CanUnpark )
        {
            _comObject.Unpark();
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(Unpark)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public void PulseGuide(GuideDirection direction, TimeSpan duration)
    {
        if (Connected && CanPulseGuide )
        {
            _comObject.PulseGuide((AscomGuideDirection)direction, (int)duration.TotalMilliseconds);
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(PulseGuide)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public void SyncRaDec(double ra, double dec)
    {
        // prevent syncs on other side of meridian (most mounts do not support that).
        if (Connected && CanSync && Tracking && !AtPark && DestinationSideOfPier(ra, dec) == SideOfPier )
        {
            _comObject.SyncToCoordinates(ra, dec);
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(SyncRaDec)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public void AbortSlew()
    {
        if (Connected )
        {
            _comObject.AbortSlew();
        }
        else
        {
            throw new InvalidOperationException($"Failed to execute {nameof(AbortSlew)} connected={Connected} initialized={_comObject is not null}");
        }
    }

    public bool CanMoveAxis(TelescopeAxis axis) => _comObject.CanMoveAxis((AscomTelescopeAxis)axis);

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis)
    {
        throw new NotImplementedException();
    }

    public void MoveAxis(TelescopeAxis axis, double rate)
    {
        throw new NotImplementedException();
    }
}