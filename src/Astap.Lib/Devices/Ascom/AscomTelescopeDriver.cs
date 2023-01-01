using System;
using System.Collections.Generic;
using Astap.Lib.Astrometry;

namespace Astap.Lib.Devices.Ascom
{
    public class AscomTelescopeDriver : AscomDeviceDriverBase, IMountDriver
    {
        private readonly Dictionary<TrackingSpeed, DriveRate> _trackingSpeedMapping = new();

        public AscomTelescopeDriver(AscomDevice device) : base(device)
        {
            DeviceConnectedEvent += AscomTelescopeDriver_DeviceConnectedEvent;
        }

        private void AscomTelescopeDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
        {
            if (e.Connected && _comObject is { } obj)
            {
                _trackingSpeedMapping.Clear();

                if (obj.TrackingRates?.Count is int count && count > 0)
                {
                    foreach (DriveRate driveRate in obj.TrackingRates)
                    {
                        var trackingSpeed = DriveRateToTrackingSpeed(driveRate);

                        if (trackingSpeed != TrackingSpeed.None)
                        {
                            _trackingSpeedMapping[trackingSpeed] = driveRate;
                        }
                    }
                }

                CanSetTracking = obj.CanSetTracking is bool canSetTracking && canSetTracking;
                CanSetSideOfPier = obj.CanSetPierSide is bool canSetSideOfPier && canSetSideOfPier;
                CanPark = obj.CanPark is bool canPark && canPark;
                CanUnpark = obj.CanUnpark is bool canUnpark && canUnpark;
                CanSetPark = obj.CanSetPark is bool canSetPark && canSetPark;
                CanSlew = obj.CanSlew is bool canSlew && canSlew;
                CanSlewAsync = obj.CanSlewAsync is bool canSlewAsync && canSlewAsync;
            }
        }

        private static TrackingSpeed DriveRateToTrackingSpeed(DriveRate driveRate)
        {
            return driveRate switch
            {
                DriveRate.Sidereal => TrackingSpeed.Sidereal,
                DriveRate.Solar => TrackingSpeed.Solar,
                DriveRate.Lunar => TrackingSpeed.Lunar,
                _ => TrackingSpeed.None
            };
        }

        public bool SlewRaDecAsync(double ra, double dec)
        {
            if (_comObject?.CanSlewAsync is bool canSlewAsync && canSlewAsync)
            {
                try
                {
                    _comObject.SlewToCoordinatesAsync(ra, dec);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public IReadOnlyCollection<TrackingSpeed> TrackingSpeeds => _trackingSpeedMapping.Keys;

        public TrackingSpeed TrackingSpeed
        {
            get => _comObject?.TrackingRate is DriveRate driveRate ? DriveRateToTrackingSpeed(driveRate) : TrackingSpeed.None;
            set
            {
                if (_trackingSpeedMapping.TryGetValue(value, out var driveRate) && _comObject is { } obj)
                {
                    obj.TrackingRate = driveRate;
                }
            }
        }

        public bool AtHome => _comObject?.AtHome is bool atHome && atHome;

        public bool AtPark => _comObject?.AtPark is bool atPark && atPark;

        public bool IsSlewing => _comObject?.Slewing is bool slewing && slewing;

        public double SiderealTime => _comObject?.SiderealTime is double siderealTime ? siderealTime : double.NaN;

        public bool TimeSuccessfullySynchronised { get; private set; }

        public DateTime? UTCDate
        {
            get => _comObject?.UTCDate is DateTime utcDate ? utcDate : null;
            set
            {
                if (_comObject is { } obj)
                {
                    obj.UTCDate = value;
                    TimeSuccessfullySynchronised = true;
                }
            }
        }

        public bool Tracking
        {
            get => _comObject?.Tracking is bool tracking && tracking;
            set
            {
                if (_comObject is { } obj)
                {
                    if (obj.CanSetTracking is false)
                    {
                        throw new InvalidOperationException("Driver does not support setting tracking");
                    }
                    obj.Tracking = value;
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

        public PierSide SideOfPier
        {
            get => _comObject?.SideOfPier is int sop ? (PierSide)sop : PierSide.Unknown;
            set
            {
                if (CanSetSideOfPier && _comObject is { } obj)
                {
                    obj.SideOfPier = value;
                }
                else
                {
                    throw new InvalidOperationException("Cannot set side of pier to: " + value);
                }
            }
        }

        public PierSide DestinationSideOfPier(double ra, double dec)
            => _comObject?.DestinationSideOfPier(ra, dec) is int dsop ? (PierSide)dsop : PierSide.Unknown;

        public EquatorialCoordinateType EquatorialSystem => Connected && _comObject?.EquatorialSystem is int es ? (EquatorialCoordinateType)es : EquatorialCoordinateType.Other;

        public double RightAscension => _comObject?.RightAscension is double ra ? ra : double.NaN;

        public double Declination => _comObject?.Declination is double dec ? dec : double.NaN;

        public double SiteElevation
        {
            get => _comObject?.SiteElevation is double siteElevation ? siteElevation : double.NaN;
            set
            {
                if (_comObject is { } obj)
                {
                    obj.SiteElevation = value;
                }
            }
        }

        public double SiteLatitude
        {
            get => _comObject?.SiteLatitude is double siteLatitude ? siteLatitude : double.NaN;
            set
            {
                if (_comObject is { } obj)
                {
                    obj.SiteLatitude = value;
                }
            }
        }

        public double SiteLongitude
        {
            get => _comObject?.SiteLongitude is double siteLongitude ? siteLongitude : double.NaN;
            set
            {
                if (_comObject is { } obj)
                {
                    obj.SiteLongitude = value;
                }
            }
        }

        public bool Park()
        {
            if (Connected && CanPark && _comObject is { } obj)
            {
                obj.Park();
                return true;
            }

            return false;
        }
        public bool Unpark()
        {
            if (Connected && CanUnpark && _comObject is { } obj)
            {
                obj.Unpark();
                return !AtPark;
            }

            return false;
        }
    }
}