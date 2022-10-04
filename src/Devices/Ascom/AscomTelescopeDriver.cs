using System;
using System.Collections.Generic;
using System.Threading;

namespace Astap.Lib.Devices.Ascom
{
    enum DriveRate
    {
        Sidereal = 0, // Sidereal tracking rate (15.041 arcseconds per second).
        Lunar  = 1, // Lunar tracking rate (14.685 arcseconds per second).
        Solar = 2, // Solar tracking rate (15.0 arcseconds per second).
        King = 3 // King tracking rate (15.0369 arcseconds per second).
    }

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
            if (e.Connected)
            {
                _trackingSpeedMapping.Clear();

                if (_comObject?.TrackingRates?.Count is int count)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (_comObject?.TrackingRates.Item[i] is DriveRate driveRate)
                        {
                            var trackingSpeed = DriveRateToTrackingSpeed(driveRate);

                            if (trackingSpeed != TrackingSpeed.None)
                            {
                                _trackingSpeedMapping[trackingSpeed] = driveRate;
                            }
                        }
                    }
                }
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

    }
}