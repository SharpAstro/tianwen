using System;
using System.Collections.Generic;

namespace Astap.Lib.Devices.Ascom;

public class AscomFilterWheelDriver : AscomDeviceDriverBase, IFilterWheelDriver
{
    public AscomFilterWheelDriver(AscomDevice device) : base(device)
    {

    }

    public IEnumerable<string> Names => EnumerateProperty<string>(nameof(Names));

    public int Position
    {
        get => _comObject?.Position is int pos ? pos : -1;
        set
        {
            if (_comObject is not null)
            {
                _comObject.Position = value;
            }
            else
            {
                throw new InvalidOperationException("Cannot change filter wheel position");
            }
        }
    }
}