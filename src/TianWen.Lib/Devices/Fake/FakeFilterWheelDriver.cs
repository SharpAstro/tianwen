using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFilterWheelDriver
{
    public IReadOnlyList<Filter> Filters => [
        new Filter("Luminance"),
        new Filter("H-Alpha + OIII", +11),
        new Filter("Red", +20),
        new Filter("Green"),
        new Filter("Blue", -15),
        new Filter("SII", +25),
        new Filter("H-Alpha", +21),
        new Filter("OIII", -3)
    ];

    public int Position => _isMoving ? -1 : _position;

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default) => BeginSetPositionAsync(position, cancellationToken);
}