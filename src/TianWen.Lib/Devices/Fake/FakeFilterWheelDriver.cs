using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFilterWheelDriver
{
    public IReadOnlyList<InstalledFilter> Filters => [
        new InstalledFilter("Luminance"),
        new InstalledFilter("H-Alpha + OIII", +11),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -15),
        new InstalledFilter("SII", +25),
        new InstalledFilter("H-Alpha", +21),
        new InstalledFilter("OIII", -3)
    ];

    public int Position => _isMoving ? -1 : _position;

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default) => BeginSetPositionAsync(position, cancellationToken);
}