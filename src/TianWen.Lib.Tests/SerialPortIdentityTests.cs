using System.Collections.Generic;
using SharpAstro.Serial;
using Shouldly;
using TianWen.Lib.Devices.Discovery;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Whether a port's USB serial may name its device in an id (#1090 step 2): only when no other port listed in the same
/// enumeration reports it too. Clone FT232 adapters ship one serial on every unit (<c>A50285BI</c>, the one Serial.Lib's own
/// fixture uses), so that key names no unit in particular.
/// </summary>
public class SerialPortIdentityTests
{
    private static SerialPortInfo Usb(string port, string serial, ushort vendor = 0x0403, ushort product = 0x6001)
        => new(port) { VendorId = vendor, ProductId = product, SerialNumber = serial };

    private static SerialPortIdentity Identity(SerialPortInfo info, params SerialPortInfo[] listed)
        => SerialPortIdentity.Of(info, listed);

    [Fact]
    public void ASerialOnlyOnePortReportsNamesItsDevice()
    {
        var cable = Usb("COM14", "A10MOSEFA");

        var identity = Identity(cable, cable, Usb("COM9", "B20OTHER"));

        identity.Kind.ShouldBe(SerialIdentityKind.Device);
        identity.Key.ShouldBe("usb:0403:6001:A10MOSEFA");
        identity.SharedWithAnotherPort.ShouldBeFalse();
        identity.NamesTheDeviceInItsId.ShouldBeTrue();
    }

    [Fact]
    public void ASerialTwoPortsReportIsSharedByBothAndNamesNeither()
    {
        var first = Usb("COM5", "A50285BI");
        var second = Usb("COM6", "A50285BI");

        foreach (var identity in new[] { Identity(first, first, second), Identity(second, first, second) })
        {
            identity.Kind.ShouldBe(SerialIdentityKind.Device, "what the port reports is still noted");
            identity.SharedWithAnotherPort.ShouldBeTrue();
            identity.NamesTheDeviceInItsId.ShouldBeFalse();
        }
    }

    [Fact]
    public void TheSameSerialOnAnotherChipIsAnotherKey()
    {
        var ftdi = Usb("COM5", "A50285BI");
        var prolific = Usb("COM6", "A50285BI", vendor: 0x067b, product: 0x23d3);

        Identity(ftdi, ftdi, prolific).SharedWithAnotherPort.ShouldBeFalse("vendor and product are part of the key");
    }

    [Fact]
    public void APortListedTwiceUnderOneNameIsNotSharedWithItself()
    {
        var cable = Usb("COM14", "A10MOSEFA");

        Identity(cable, cable, Usb("com14", "A10MOSEFA")).SharedWithAnotherPort.ShouldBeFalse();
    }

    [Fact]
    public void AUnitUnpluggedSinceIsNotCounted()
    {
        // The list is one enumeration's: the second unit is gone from it, so the first is alone on its serial again.
        var first = Usb("COM5", "A50285BI");

        Identity(first, first).NamesTheDeviceInItsId.ShouldBeTrue();
    }

    /// <summary>A socket key names the device too since #2040 (the same whatever COM number Windows hands it); a bare name never does.</summary>
    [Fact]
    public void ASocketKeyNamesTheDeviceInItsIdAndANameKeyNever()
    {
        var socket = new SerialPortInfo("COM13") { LocationPath = "PCIROOT(0)#USB(3)" };
        var plain = new SerialPortInfo("COM1");

        IReadOnlyList<SerialPortInfo> listed = [socket, plain];
        SerialPortIdentity.Of(socket, listed).NamesTheDeviceInItsId.ShouldBeTrue();
        SerialPortIdentity.Of(plain, listed).NamesTheDeviceInItsId.ShouldBeFalse();
    }
}
