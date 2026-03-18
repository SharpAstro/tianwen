using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ManualFilterWheelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenLUltimateFilterWhenConnectedThenReportsSingleFilter()
    {
        var device = new ManualFilterWheelDevice(Filter.HydrogenAlphaOxygenIII);
        var external = new FakeExternal(output);
        device.TryInstantiateDriver<IFilterWheelDriver>(external, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync();

        driver.Connected.ShouldBeTrue();
        driver.Filters.Count.ShouldBe(1);
        driver.Filters[0].Filter.ShouldBe(Filter.HydrogenAlphaOxygenIII);
        driver.Filters[0].Filter.ShortName.ShouldBe("Hα+OIII");
    }

    [Fact]
    public async Task GivenManualFilterWhenGetPositionThenAlwaysReturnsZero()
    {
        var device = new ManualFilterWheelDevice(Filter.Red);
        var external = new FakeExternal(output);
        device.TryInstantiateDriver<IFilterWheelDriver>(external, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync();

        var position = await driver.GetPositionAsync();
        position.ShouldBe(0);

        // BeginMoveAsync is a no-op
        await driver.BeginMoveAsync(0);
        (await driver.GetPositionAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task GivenManualFilterWhenGetCurrentFilterThenReturnsInstalledFilter()
    {
        var device = new ManualFilterWheelDevice(Filter.Luminance);
        var external = new FakeExternal(output);
        device.TryInstantiateDriver<IFilterWheelDriver>(external, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync();

        var current = await driver.GetCurrentFilterAsync();
        current.Filter.ShouldBe(Filter.Luminance);
    }

    [Fact]
    public void GivenFilterNameStringWhenCreatingDeviceThenFilterIsParsed()
    {
        var device = new ManualFilterWheelDevice("H-Alpha");
        device.InstalledFilter.ShouldBe(Filter.HydrogenAlpha);
    }

    [Fact]
    public void GivenDeviceUriWhenCreatingDeviceThenFilterIsEncodedInQuery()
    {
        var device = new ManualFilterWheelDevice(Filter.SulphurII);
        device.DeviceUri.Query.ShouldContain("filter1=SulphurII");
        device.InstalledFilter.ShouldBe(Filter.SulphurII);
    }
}
