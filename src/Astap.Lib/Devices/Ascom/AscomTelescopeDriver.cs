using System;
using System.Collections.Generic;
using System.Threading;

namespace Astap.Lib.Devices.Ascom
{
    public class AscomTelescopeDriver : AscomDeviceDriverBase, IMountDriver
    {
        private readonly Dictionary<TrackingSpeed, DriveRate> _trackingSpeedMapping = new();

        private Exception? _lastException;

        public AscomTelescopeDriver(AscomDevice device) : base(device)
        {
            DeviceConnectedEvent += AscomTelescopeDriver_DeviceConnectedEvent;
        }

        private void AscomTelescopeDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
        {
            if (e.Connected && _comObject is var obj and not null)
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

        public bool SlewAsync(double ra, double dec)
        {
            if (_comObject?.CanSlewAsync is bool canSlewAsync && canSlewAsync)
            {
                try
                {
                    _comObject.SlewToCoordinatesAsync(ra, dec);
                    return true;
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref _lastException, e);
                    return false;
                }
            }

            return false;
        }

        public TrackingSpeed TrackingSpeed
        {
            get => _comObject?.TrackingRate is DriveRate driveRate ? DriveRateToTrackingSpeed(driveRate) : TrackingSpeed.None;
            set
            {
                if (_trackingSpeedMapping.TryGetValue(value, out var driveRate) && _comObject is var obj and not null)
                {
                    obj.TrackingRate = driveRate;
                }
            }
        }

        public bool AtHome => _comObject?.AtHome is bool atHome && atHome;

        public bool IsSlewing => _comObject?.Slewing is bool slewing && slewing;

        public double SiderealTime => _comObject?.SiderealTime is double siderealTime ? siderealTime : double.NaN;

        public DateTime? UTCDate
        {
            get => _comObject?.UTCDate is DateTime utcDate ? utcDate : null;
            set
            {
                if (_comObject is var obj and not null)
                {
                    try
                    {
                        obj.UTCDate = value;
                    }
                    catch (Exception e)
                    {
                        Interlocked.Exchange(ref _lastException, e);
                    }
                }
            }
        }

        public bool Tracking
        {
            get => _comObject?.Tracking is bool tracking && tracking;
            set
            {
                if (_comObject is var obj and not null)
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

        public bool CanSetPark { get; private set; }

        public bool CanSlew { get; private set; }

        public bool CanSlewAsync { get; private set; }

        public PierSide SideOfPier
        {
            get => _comObject?.SideOfPier is PierSide sop ? sop : PierSide.Unknown;
            set
            {
                if (CanSetSideOfPier && _comObject is var obj and not null)
                {
                    obj.SideOfPier = value;
                }
                else
                {
                    throw new InvalidOperationException("Cannot set side of pier to: " + value);
                }
            }
        }
    }
}