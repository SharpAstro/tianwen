using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Device")]
public class ManualFilterWheelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenLUltimateFilterWhenConnectedThenReportsSingleFilter()
    {
        var device = new ManualFilterWheelDevice(Filter.HydrogenAlphaOxygenIII);
        var external = new FakeExternal(output);
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<IFilterWheelDriver>(sp, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

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
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<IFilterWheelDriver>(sp, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

        var position = await driver.GetPositionAsync(TestContext.Current.CancellationToken);
        position.ShouldBe(0);

        // BeginMoveAsync is a no-op
        await driver.BeginMoveAsync(0, TestContext.Current.CancellationToken);
        (await driver.GetPositionAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task GivenManualFilterWhenGetCurrentFilterThenReturnsInstalledFilter()
    {
        var device = new ManualFilterWheelDevice(Filter.Luminance);
        var external = new FakeExternal(output);
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<IFilterWheelDriver>(sp, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

        var current = await driver.GetCurrentFilterAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public void ManualFilterWheelDevice_roundtrips_through_the_keyed_uri_factory()
    {
        // The profile -> session path (SessionFactory.DeviceFromUri) resolves a stored filter-wheel URI via
        // IDeviceHub.TryGetDeviceFromUri, keyed on the URI host. AddDevices() registers the factory so a
        // ManualFilterWheelDevice URI reconstructs as a ManualFilterWheelDevice (and would otherwise throw),
        // with the installed filter surviving on the query.
        var sp = new ServiceCollection()
            .AddLogging()
            .AddDevices()
            .BuildServiceProvider();
        var hub = sp.GetRequiredService<IDeviceHub>();

        var stored = new ManualFilterWheelDevice(Filter.HydrogenAlpha).DeviceUri;
        hub.TryGetDeviceFromUri(stored, out var device).ShouldBeTrue();
        var resolved = device.ShouldBeOfType<ManualFilterWheelDevice>();
        resolved.DeviceType.ShouldBe(DeviceType.FilterWheel);
        resolved.InstalledFilter.ShouldBe(Filter.HydrogenAlpha);
    }
}
