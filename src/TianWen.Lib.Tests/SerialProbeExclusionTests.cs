using SharpAstro.Serial;
using Shouldly;
using TianWen.Lib.Devices.Discovery;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which ports a discovery does not probe: measured on a machine whose two COM ports were both Bluetooth, a pair of
/// headphones ("S42", Class of Device 0x240404) that took every write and answered none for 31 s of each discovery, and
/// Windows' own incoming port.
/// </summary>
public class SerialProbeExclusionTests
{
    private static SerialPortInfo Bluetooth(ulong address, uint? classOfDevice, string? name = null)
        => new SerialPortInfo("COM4") { Bluetooth = new SerialBluetoothDevice(address) { ClassOfDevice = classOfDevice, Name = name } };

    [Fact]
    public void A_pair_of_headphones_is_not_probed_and_the_reason_names_it()
    {
        var reason = SerialProbeExclusion.ReasonNotToProbe(Bluetooth(0x83CD1DB5D95D, 0x240404, "S42")).ShouldNotBeNull();

        reason.ShouldContain("audio device");
        reason.ShouldContain("S42");
    }

    [Fact]
    public void Windows_incoming_Bluetooth_port_is_not_probed()
    {
        SerialProbeExclusion.ReasonNotToProbe(Bluetooth(0, null)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0x7A020Cu)] // a phone
    [InlineData(0x380104u)] // a computer
    [InlineData(0x002540u)] // a keyboard
    [InlineData(0x000704u)] // a watch
    public void A_device_whose_class_is_never_an_instrument_is_not_probed(uint classOfDevice)
    {
        SerialProbeExclusion.ReasonNotToProbe(Bluetooth(0x0015838D58DC, classOfDevice)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0x001F00u)] // an HC-05 serial module's default: uncategorised
    [InlineData(0x000000u)] // miscellaneous, or no class given
    [InlineData(null)]      // no record of the device at all
    public void A_serial_module_or_a_device_that_does_not_say_is_probed(uint? classOfDevice)
    {
        SerialProbeExclusion.ReasonNotToProbe(Bluetooth(0x98D331F5A1B2, classOfDevice, "HC-05")).ShouldBeNull();
    }

    [Fact]
    public void A_port_that_is_not_Bluetooth_is_probed()
    {
        SerialProbeExclusion.ReasonNotToProbe(new SerialPortInfo("COM3") { VendorId = 0x1A86, ProductId = 0x7523 }).ShouldBeNull();
    }
}
