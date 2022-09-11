using Astap.Lib.Devices;
using System;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public abstract class FilterWheelBase<TDevice, TDriver> : ControllableDeviceBase<TDevice, TDriver>
        where TDevice : DeviceBase
        where TDriver : IDeviceDriver
    {

        public FilterWheelBase(TDevice device) : base(device) { }

        public abstract IReadOnlyCollection<Filter> Filters { get; }

        public abstract bool? CanChange { get; }

        public abstract int Position { get; set; }
    }
}
