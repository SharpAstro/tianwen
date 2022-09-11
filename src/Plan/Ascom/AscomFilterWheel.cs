using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Plan.Ascom
{
    public class AscomFilterWheel : FilterWheelBase<AscomDevice, AscomFilterWheelDriver>
    {
        public AscomFilterWheel(AscomDevice device)
            : base(device.DeviceType == DeviceBase.FilterWheelType ? device : throw new ArgumentException("Device is not an ASCOM filter wheel", nameof(device)))
        {
            Filters = Driver.Names.Select(p => new Filter(p)).ToList();
        }

        public override IReadOnlyCollection<Filter> Filters { get; }

        public override bool? CanChange => true;

        public override int Position
        {
            get => Driver?.Position ?? -1;
            set => Driver.Position = value;
        }
    }
}
