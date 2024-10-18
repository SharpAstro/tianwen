using System;
using System.Collections.Generic;
using System.Linq;

namespace TianWen.Lib.Devices.Ascom;

public class AscomFilterWheelDriver(AscomDevice device, IExternal external) : AscomDeviceDriverBase(device, external), IFilterWheelDriver
{
    string[] Names => _comObject?.Names is string[] names ? names : [];

    int[] FocusOffsets => _comObject?.FocusOffsets is int[] focusOffsets ? focusOffsets : [];

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

    public IReadOnlyList<Filter> Filters
    {
        get
        {
            var names = Names;
            var offsets = FocusOffsets;
            var filters = new List<Filter>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                filters[i] = new Filter(names[i], i < offsets.Length ? offsets[i] : 0);
            }

            return filters;
        }
    }
}