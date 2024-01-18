using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomFilterWheelDriver(AscomDevice device) : AscomDeviceDriverBase(device), IFilterWheelDriver
{
    string[] Names => _comObject?.Names is string[] names ? names : [];

    public int Position
    {
        get => _comObject?.Position is int pos ? pos : -1;
        set
        {
            if (_comObject is { } obj && Filters is { Count: > 0 } filters && value is >= 0 && value < filters.Count)
            {
                obj.Position = value;
            }
            else
            {
                throw new InvalidOperationException($"Cannot change filter wheel position to {value}");
            }
        }
    }

    public IReadOnlyList<Filter> Filters => Names.Select(p => new Filter(p)).ToList();
}