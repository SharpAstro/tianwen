using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomFilterWheelDriver : AscomDeviceDriverBase, IFilterWheelDriver
{
    private readonly List<Filter> _filters = new();

    public AscomFilterWheelDriver(AscomDevice device) : base(device)
    {
        DeviceConnectedEvent += AscomFilterWheelDriver_DeviceConnectedEvent;
    }

    private void AscomFilterWheelDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            _filters.Clear();
            _filters.AddRange(EnumerateProperty<string>("Names").Where(p => !string.IsNullOrEmpty(p)).Select(p => new Filter(p)));
        }
    }

    public int Position
    {
        get => _comObject?.Position is int pos ? pos : -1;
        set
        {
            if (_comObject is not null && value is >= 0 && value < Filters.Count)
            {
                _comObject.Position = value;
            }
            else
            {
                throw new InvalidOperationException($"Cannot change filter wheel position to {value}");
            }
        }
    }

    public IReadOnlyCollection<Filter> Filters => _filters;
}