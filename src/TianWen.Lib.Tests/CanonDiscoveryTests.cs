using FC.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A Canon body over WPD is keyed by its own serial, not by the USB path it is plugged into (#1097), and the path reaches WPD
/// as Windows wrote it, not percent-escaped (#1096). The Windows layer is faked, so this needs no camera.
/// </summary>
public class CanonDiscoveryTests
{
    // What an EOS 6D looks like to Windows: the device instance is the parent hub's ParentIdPrefix and the hub port.
    private const string PortTwo = @"\\?\usb#vid_04a9&pid_3250#6&fd1ea69&1&2#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";
    private const string PortFour = @"\\?\usb#vid_04a9&pid_3250#6&fd1ea69&1&4#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";
    private const string OtherHub = @"\\?\usb#vid_04a9&pid_3250#6&18d4b223&0&2#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";
    private const string Serial = "00112233445566778899aabbccddeeff";
    private const string OtherSerial = "ffeeddccbbaa99887766554433221100";

    private sealed class FakeWpd : ICanonWpd
    {
        public List<(string Id, string Name)> Cameras { get; } = [];

        /// <summary>The body behind each path: its identity, or null for one that reports no serial.</summary>
        public Dictionary<string, CanonBodyIdentity?> Bodies { get; } = [];

        public Exception? FailWith { get; set; }

        public List<string> Reads { get; } = [];

        public IEnumerable<(string Id, string Name)> Enumerate() => [.. Cameras];

        public Task<CanonBodyIdentity?> ReadIdentityAsync(string wpdId, CancellationToken cancellationToken)
        {
            Reads.Add(wpdId);
            cancellationToken.ThrowIfCancellationRequested();
            return FailWith is { } failure ? Task.FromException<CanonBodyIdentity?>(failure) : Task.FromResult(Bodies.GetValueOrDefault(wpdId));
        }
    }

    private static (CanonDeviceSource Source, FakeWpd Wpd, CanonBodyRegistry Bodies) Build()
    {
        var wpd = new FakeWpd();
        var bodies = new CanonBodyRegistry();
        return (new CanonDeviceSource(NullLogger<CanonDeviceSource>.Instance, wpd, bodies), wpd, bodies);
    }

    private static CanonBodyIdentity Body(string serial) => new("Canon EOS 6D", serial);

    [Fact]
    public async Task OneBodyMovedToAnotherPortIsTheSameDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, _) = Build();
        wpd.Bodies[PortTwo] = Body(Serial);
        wpd.Bodies[PortFour] = Body(Serial);

        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));
        var before = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();
        wpd.Cameras.Clear();
        wpd.Cameras.Add((PortFour, "Canon EOS 6D"));
        var after = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();

        after.DeviceUri.DeviceKey.ShouldBe(before.DeviceUri.DeviceKey, "the serial is the body's own, the port is not");
        DeviceBase.SameDevice(before.DeviceUri, after.DeviceUri).ShouldBeTrue();
        before.DeviceId.ShouldBe(Serial);
        before.WpdDeviceId.ShouldBe(PortTwo, "the URI still says where to open it today");
        after.WpdDeviceId.ShouldBe(PortFour);
        before.IsWpd.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoBodiesAreTwoDevices()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, _) = Build();
        wpd.Bodies[PortTwo] = Body(Serial);
        wpd.Bodies[OtherHub] = Body(OtherSerial);
        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));
        wpd.Cameras.Add((OtherHub, "Canon EOS 6D"));

        var found = await source.DiscoverWpdAsync(ct);

        found.Count.ShouldBe(2);
        found[0].DeviceUri.DeviceKey.ShouldNotBe(found[1].DeviceUri.DeviceKey);
    }

    [Fact]
    public async Task APathThatWasReadIsNotReadAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, _) = Build();
        wpd.Bodies[PortTwo] = Body(Serial);
        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));

        await source.DiscoverWpdAsync(ct);
        await source.DiscoverWpdAsync(ct);
        var third = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();

        wpd.Reads.ShouldBe([PortTwo], "opening the camera is what can cost another program a live view frame, so once");
        third.DeviceId.ShouldBe(Serial);
    }

    [Fact]
    public async Task ACameraADriverConnectedIsNotOpenedByDiscovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, bodies) = Build();
        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));
        // What CanonCameraDriver records once its session is open: the session reported the serial, so discovery is told.
        bodies.Remember(PortTwo, Serial);

        var found = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();

        wpd.Reads.ShouldBeEmpty("the driver holds the camera; opening it is the read that can cost its command");
        found.DeviceId.ShouldBe(Serial);
    }

    [Fact]
    public async Task ABodyThatReportsNoSerialIsKeyedByItsPathAndNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, _) = Build();
        wpd.Bodies[PortTwo] = null;
        wpd.Cameras.Add((PortTwo, "Canon EOS 450D"));

        var first = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();
        await source.DiscoverWpdAsync(ct);

        first.DeviceId.ShouldBe(Uri.EscapeDataString(PortTwo), "the older form: the path is the id");
        first.WpdDeviceId.ShouldBe(PortTwo, "and it still opens by the path as Windows wrote it");
        wpd.Reads.Count.ShouldBe(1, "a body with no serial has none the second time either");
    }

    [Fact]
    public async Task AReadThatFailsKeepsThePathKeyAndIsTriedAgainNextTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, wpd, _) = Build();
        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));
        wpd.FailWith = new InvalidOperationException("the camera is starting up");

        var failed = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();
        failed.DeviceId.ShouldBe(Uri.EscapeDataString(PortTwo), "discovery goes on, and the camera is still listed");

        wpd.FailWith = null;
        wpd.Bodies[PortTwo] = Body(Serial);
        var recovered = (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem();

        recovered.DeviceId.ShouldBe(Serial, "a failure is not remembered: it may have been a camera mid-boot");
        wpd.Reads.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ATimedOutReadIsAFailureButACancelledDiscoveryIsNot()
    {
        var (source, wpd, _) = Build();
        wpd.Cameras.Add((PortTwo, "Canon EOS 6D"));

        // The source's own deadline cancels the read's token, which the read reports as an OperationCanceledException.
        wpd.FailWith = new OperationCanceledException();
        (await source.DiscoverWpdAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem()
            .DeviceId.ShouldBe(Uri.EscapeDataString(PortTwo));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        wpd.FailWith = null;
        await Should.ThrowAsync<OperationCanceledException>(() => source.DiscoverWpdAsync(cancelled.Token));
    }

    [Fact]
    public void AnOlderUriOpensByTheWpdPathAsWindowsWroteIt()
    {
        // #1096: DeviceId is the URI's path, still percent-escaped, and WPD rejects that: ArgumentException "Value does not
        // fall within the expected range", measured on an EOS 6D while the raw path opened the session.
        var older = new CanonDevice(new Uri($"Camera://CanonDevice/{Uri.EscapeDataString(PortTwo)}?port=wpd#Canon%20EOS%206D"));

        older.DeviceId.ShouldContain("%5C", customMessage: "premise: the id is escaped");
        older.WpdDeviceId.ShouldBe(PortTwo);
        older.RawDeviceId.ShouldBe(PortTwo);
    }

    [Fact]
    public void AUsbDevicePathIdIsComparedAsTheCameraReportsIt()
    {
        // The USB branch compares the URI's id with the one it rebuilds from the camera; a device path is full of
        // characters that do not survive escaping, a serial is not.
        const string devicePath = @"\\?\usb#vid_04a9&pid_3250#0000a1b2c3#{ade1e1a6-5a9b-4fbc-9f0f-0f3f3f3f3f3f}";
        var usb = new CanonDevice(new Uri($"Camera://CanonDevice/{Uri.EscapeDataString(devicePath)}?port=usb#Canon%20EOS%206D"));
        var plainSerial = new CanonDevice(new Uri("Camera://CanonDevice/195020000089?port=usb#Canon%20EOS%206D"));

        usb.RawDeviceId.ShouldBe(devicePath);
        plainSerial.RawDeviceId.ShouldBe("195020000089");
    }

    [Fact]
    public void AStoredCameraTakesTheCurrentWpdPathFromDiscoveryAndKeepsItsKey()
    {
        // Reconcile refreshes transport state from discovery, the way a COM port swap is. The WPD path is transport state,
        // and the serial in the path is what lets the stored camera find the discovered one after the cable moved.
        var stored = new Uri($"Camera://CanonDevice/{Serial}?port=wpd&wpd={Uri.EscapeDataString(PortTwo)}#Canon%20EOS%206D");
        var discovered = new CanonDevice(new Uri($"Camera://CanonDevice/{Serial}?port=wpd&wpd={Uri.EscapeDataString(PortFour)}#Canon%20EOS%206D"));
        var discovery = Substitute.For<IDeviceDiscovery>();
        discovery.RegisteredDevices(DeviceType.Camera).Returns([discovered]);

        var reconciled = discovery.ReconcileUri(stored);

        new CanonDevice(reconciled).WpdDeviceId.ShouldBe(PortFour, "it opens where the camera is now");
        reconciled.DeviceKey.ShouldBe(stored.DeviceKey, "and is still the camera the profile named");
        DeviceQueryKey.WpdDeviceId.IsTransport.ShouldBeTrue();
    }

    // An mDNS response: no questions, each record (owner, type, rdata) an answer, every name written in full.
    private static byte[] MdnsResponse(params (string Owner, ushort Type, byte[] Data)[] records)
    {
        var bytes = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
        foreach (var (owner, type, data) in records)
        {
            bytes.AddRange(Name(owner));
            bytes.AddRange([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length]);
            bytes.AddRange(data);
        }
        return [.. bytes];
    }

    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>
    /// Only an answer to the scan's question is a camera: a PTR owned by <c>_ptp._tcp.local</c> naming a PTP instance. A
    /// Canon TS7700 printer's own announcement on the shared mDNS port was listed as a WiFi camera named
    /// <c>_FC9F5ED42C8A._tcp.local</c>, the scan having taken any packet that carried an address (#1111).
    /// </summary>
    [Fact]
    public void An_mDNS_packet_is_a_camera_only_when_it_answers_for_ptp()
    {
        byte[] address = [192, 168, 0, 201];

        var camera = MdnsResponse(
            ("_ptp._tcp.local", 12, Name("EOS 6D Mark II._ptp._tcp.local")),
            ("EOS-6D.local", 1, address));
        CanonDeviceSource.ParseMdnsResponse(camera).ShouldBe([("EOS 6D Mark II", "192.168.0.201")]);

        var printer = MdnsResponse(
            ("_services._dns-sd._udp.local", 12, Name("_FC9F5ED42C8A._tcp.local")),
            ("Canon-TS7700.local", 1, address));
        CanonDeviceSource.ParseMdnsResponse(printer).ShouldBeEmpty("an announcement of another service is no answer");

        var otherService = MdnsResponse(
            ("_ipp._tcp.local", 12, Name("Canon TS7700 series._ipp._tcp.local")),
            ("Canon-TS7700.local", 1, address));
        CanonDeviceSource.ParseMdnsResponse(otherService).ShouldBeEmpty("a printer's IPP is no PTP camera");
    }
}
