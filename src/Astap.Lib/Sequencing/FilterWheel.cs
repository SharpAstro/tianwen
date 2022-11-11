using Astap.Lib.Devices;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Sequencing;

public class FilterWheel : ControllableDeviceBase<IFilterWheelDriver>
{

    private readonly List<Filter> _filters = new();

    public FilterWheel(DeviceBase device) : base(device) { }

    public IReadOnlyCollection<Filter> Filters => _filters;

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            _filters.Clear();
            _filters.AddRange(Driver.Names.Where(p => !string.IsNullOrEmpty(p)).Select(p => new Filter(p)));
        }
    }

    public int Position
    {
        get => Driver?.Position ?? -1;
        set => Driver.Position = value;
    }
}
