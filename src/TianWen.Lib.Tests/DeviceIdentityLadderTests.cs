using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Devices;
using Xunit;
using static TianWen.Lib.Tests.DeviceDiscoveryExtensionsTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// The identity ladder's matching table (#2040; docs/architecture/device-architecture.md, "Step 3: match on the evidence"),
/// one row per line: what a profile stored, what discovery found, whether reconcile follows and what the node's notification
/// says. A change to the order of the rungs fails a row.
/// </summary>
public class DeviceIdentityLadderTests
{
    /// <summary>
    /// A Sky-Watcher mount as discovery writes it (<see cref="Lib.Devices.Discovery.SerialPortIdentity.IdSuffixOf"/>): the id ends
    /// in what names the port (the USB bridge's serial for <c>usb:</c>, the socket for <c>socket:</c>, else the COM name), the
    /// port's notes and the firmware ride in the query.
    /// </summary>
    private static string Mount(string model, string port, string firmware, string? portKey = null)
    {
        var kind = portKey switch { null => "name", _ when portKey.StartsWith("usb:", StringComparison.Ordinal) => "device", _ => "socket" };
        var key = portKey ?? $"name:{port}";
        var idEnd = Lib.Devices.Discovery.SerialPortIdentity.IdSuffixOf(kind, key) ?? port;
        return $"Mount://SkywatcherDevice/Skywatcher_{model}_{idEnd}?port=serial:{port}&baud=115200&portIdentity={kind}&portKey={Uri.EscapeDataString(key)}&firmware={firmware}#Skywatcher {model} (FW {firmware})";
    }

    private const string Cable = "usb:1a86:7523:CAB1";

    public static TheoryData<string, string, string[], string?, string?> Rows => new()
    {
        {
            "id, model and firmware: the same device, silently",
            Mount("AZ-GTi", "COM5", "3.39", Cable),
            [Mount("AZ-GTi", "COM5", "3.39", Cable)],
            "Skywatcher_AZ-GTi_usb-1a86-7523-CAB1", null
        },
        {
            "the same id on another COM number: transport, silently",
            Mount("AZ-GTi", "COM5", "3.39", Cable),
            [Mount("AZ-GTi", "COM9", "3.39", Cable)],
            "Skywatcher_AZ-GTi_usb-1a86-7523-CAB1", null
        },
        {
            "id and model, firmware changed: followed, the profile takes the new firmware, and says so",
            Mount("AZ-GTi", "COM5", "3.39", Cable),
            [Mount("AZ-GTi", "COM5", "3.40", Cable)],
            "Skywatcher_AZ-GTi_usb-1a86-7523-CAB1", "firmware 3.39 to 3.40"
        },
        {
            "the bridge's serial now reports ANOTHER model: the cable moved to another mount, never followed",
            Mount("AZ-GTi", "COM5", "3.39", Cable),
            [Mount("EQ6-R", "COM5", "3.39", Cable)],
            null, null
        },
        {
            "model only, the one candidate: re-plugged in another socket, followed, and says so",
            Mount("EQ6-R", "COM3", "3.39", "socket:USB(3)"),
            [Mount("EQ6-R", "COM4", "3.39", "socket:USB(4)")],
            "Skywatcher_EQ6-R_" + Lib.Devices.Discovery.SerialPortIdentity.IdSuffixOf("socket", "socket:USB(4)"), "followed to COM4 (was COM3)"
        },
        {
            "model only, the one candidate, firmware changed too: the selected profile's device, churn accepted",
            Mount("EQ6-R", "COM3", "3.39"),
            [Mount("EQ6-R", "COM4", "3.40")],
            "Skywatcher_EQ6-R_COM4", "firmware 3.39 to 3.40"
        },
        {
            "several of the model, the firmware tells one: followed",
            Mount("EQ6-R", "COM3", "3.39"),
            [Mount("EQ6-R", "COM4", "3.40"), Mount("EQ6-R", "COM6", "3.39")],
            "Skywatcher_EQ6-R_COM6", "followed to COM6 (was COM3)"
        },
        {
            "several of the model and the firmware tells none: never guessed",
            Mount("EQ6-R", "COM3", "3.39"),
            [Mount("EQ6-R", "COM4", "3.39"), Mount("EQ6-R", "COM6", "3.39")],
            null, null
        },
        {
            "an id that names the hardware (a UUID) is never followed by its model",
            "Mount://OnStepDevice/On-Step_abc-uuid?port=serial:COM5&firmware=10.24#On-Step",
            ["Mount://OnStepDevice/On-Step_def-uuid?port=serial:COM5&firmware=10.24#On-Step"],
            null, null
        },
    };

    /// <summary>
    /// The LX200 family's one id (Meade, OnStep; serial and network alike): the product and the UUID, else the product and the
    /// transport. Never the firmware number (<c>:GVN#</c>), which used to sit beside even the UUID, nor the site names.
    /// </summary>
    [Theory]
    [InlineData("LX85", "f3a1c2", "COM5", "LX85_f3a1c2")]
    [InlineData("On-Step", "f3a1c2", "192.168.1.20", "On-Step_f3a1c2")]
    [InlineData("LX85", null, "COM5", "LX85_COM5")]
    [InlineData("On-Step", null, "192.168.1.20", "On-Step_192.168.1.20")]
    [InlineData("LX_200/GPS", null, "/dev/ttyUSB0", "LX-200-GPS_-dev-ttyUSB0")]
    public void AnLx200IdIsTheProductAndTheUuidElseTheTransport(string product, string? uuid, string transport, string id)
        => Lib.Devices.Meade.Lx200DeviceId.Of(product, uuid, transport).ShouldBe(id);

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheLadderDecides(string row, string stored, string[] discovered, string? followedTo, string? note)
    {
        var storedUri = new Uri(stored);
        var discovery = new StubDiscovery { Mounts = [.. discovered.Select(d => new FakeDeviceRecord(new Uri(d)))] };
        var before = new ProfileData(Mount: storedUri, Guider: new Uri("Guider://NoneDevice/none"), OTAs: []);

        var (after, _) = discovery.ReconcileProfileData(before, ProfileFollow.ForSelected());

        if (followedTo is null)
        {
            after.Mount.ShouldBe(storedUri, row);
        }
        else
        {
            after.Mount.AbsolutePath.ShouldEndWith(followedTo, Case.Insensitive, row);
        }

        var notes = DeviceFollowNotes.For(before, after).ToList();
        if (note is null)
        {
            notes.ShouldBeEmpty(row);
        }
        else
        {
            notes.ShouldHaveSingleItem(row).ShouldContain(note, Case.Sensitive, row);
        }
    }

    private static ProfileData Profile(string mount) => new(Mount: new Uri(mount), Guider: new Uri("Guider://NoneDevice/none"), OTAs: []);

    private static StubDiscovery Found(string[] discovered) => new() { Mounts = [.. discovered.Select(d => new FakeDeviceRecord(new Uri(d)))] };

    /// <summary>The selected profile's reconcile (<see cref="ProfileFollow.ForSelected"/>) unless another rule is given.</summary>
    private static (ProfileData Before, ProfileData After) Reconcile(string stored, string[] discovered, ProfileFollow? follow = null)
    {
        var before = Profile(stored);
        return (before, Found(discovered).ReconcileProfileData(before, follow ?? ProfileFollow.ForSelected()).Reconciled);
    }

    [Fact]
    public void AFirmwareUpdateTakesTheNewDisplayNameAndTheNoteNamesIt()
    {
        var (before, after) = Reconcile(Mount("AZ-GTi", "COM5", "3.39", Cable), [Mount("AZ-GTi", "COM5", "3.40", Cable)]);

        Uri.UnescapeDataString(after.Mount.Fragment).ShouldBe("#Skywatcher AZ-GTi (FW 3.40)");
        DeviceFollowNotes.For(before, after).ShouldHaveSingleItem().ShouldBe("Skywatcher AZ-GTi (FW 3.40): firmware 3.39 to 3.40");
    }

    [Fact]
    public void AProbeThatReadNoFirmwareKeepsTheStoredOne()
    {
        var noFirmware = Mount("AZ-GTi", "COM9", "3.39", Cable).Replace("&firmware=3.39", "");
        var (_, after) = Reconcile(Mount("AZ-GTi", "COM5", "3.39", Cable), [noFirmware]);

        after.Mount.QueryValue(DeviceQueryKey.Firmware).ShouldBe("3.39");
        after.Mount.QueryValue(DeviceQueryKey.Port).ShouldBe("serial:COM9");
    }

    /// <summary>
    /// The evidence rungs (one candidate of the model, or the one of several with the stored firmware) are the selected
    /// profile's alone: another rig's profile is not rewritten by a unit that may not be its own, by the model or the firmware.
    /// </summary>
    [Fact]
    public void TheEvidenceRungsAreTheSelectedProfilesAlone()
    {
        var stored = Mount("EQ6-R", "COM3", "3.39");
        string[] lone = [Mount("EQ6-R", "COM4", "3.39")];
        string[] several = [Mount("EQ6-R", "COM4", "3.40"), Mount("EQ6-R", "COM6", "3.39")];

        Reconcile(stored, lone).After.Mount.AbsolutePath.ShouldEndWith("COM4", Case.Insensitive);
        Reconcile(stored, several).After.Mount.AbsolutePath.ShouldEndWith("COM6", Case.Insensitive);
        Reconcile(stored, lone, ProfileFollow.ForSelected().ForAnother()).After.Mount.ShouldBe(new Uri(stored));
        Reconcile(stored, several, ProfileFollow.ForSelected().ForAnother()).After.Mount.ShouldBe(new Uri(stored));
        Reconcile(stored, lone, ProfileFollow.WithoutSelected()).After.Mount.ShouldBe(new Uri(stored));
    }

    /// <summary>
    /// One unit moved is moved for every profile that holds it (the owner, 2026-11-07): a profile that is not selected follows
    /// the id the selected one followed, to that same unit, with its own note in the feed; one holding another id is left alone, even
    /// where the firmware would have told its unit apart.
    /// </summary>
    [Fact]
    public void AnotherProfileFollowsOnlyTheUnitTheSelectedOneFollowed()
    {
        var discovery = Found([Mount("EQ6-R", "COM4", "3.40"), Mount("EQ6-R", "COM7", "3.39")]);
        var selected = ProfileFollow.ForSelected();
        var rigA = Profile(Mount("EQ6-R", "COM3", "3.40"));
        var sameUnit = Profile(Mount("EQ6-R", "COM3", "3.40"));
        var otherUnit = Profile(Mount("EQ6-R", "COM6", "3.39"));

        discovery.ReconcileProfileData(rigA, selected).Reconciled.Mount.AbsolutePath.ShouldEndWith("COM4", Case.Insensitive);
        var (followed, changed) = discovery.ReconcileProfileData(sameUnit, selected.ForAnother());
        var (leftAlone, untouched) = discovery.ReconcileProfileData(otherUnit, selected.ForAnother());

        changed.ShouldBeTrue();
        followed.Mount.AbsolutePath.ShouldEndWith("COM4", Case.Insensitive);
        DeviceFollowNotes.For(sameUnit, followed).ShouldHaveSingleItem().ShouldContain("followed to COM4 (was COM3)");
        untouched.ShouldBeFalse();
        leftAlone.ShouldBe(otherUnit);
    }

    /// <summary>
    /// An id that changed only its shape where the device sits (a serial-less port, keyed on its COM name before #2040 and on
    /// its socket after) is followed, but moved nothing, so the feed says nothing: no "followed" line for every serial-less
    /// device once after the upgrade.
    /// </summary>
    [Fact]
    public void AnIdReKeyedOnTheSamePortIsFollowedSilently()
    {
        var (before, after) = Reconcile(Mount("EQ6-R", "COM3", "3.39"), [Mount("EQ6-R", "COM3", "3.39", "socket:USB(3)")]);

        after.Mount.AbsolutePath.ShouldEndWith(Lib.Devices.Discovery.SerialPortIdentity.IdSuffixOf("socket", "socket:USB(3)") ?? "", Case.Insensitive);
        DeviceFollowNotes.For(before, after).ShouldBeEmpty();
    }

    /// <summary>
    /// Another unit of the model on another cable that Windows handed the same COM number is followed by the model alone (the
    /// selected profile), and that IS a follow: the note names the ids, since the port reads the same (#2048 review).
    /// </summary>
    [Fact]
    public void AnotherUnitOnTheSameComNumberIsFollowedAndSaidByItsIds()
    {
        const string aaa = "usb:0403:6001:AAA", bbb = "usb:0403:6001:BBB";
        var (before, after) = Reconcile(Mount("AZ-GTi", "COM5", "3.39", aaa), [Mount("AZ-GTi", "COM5", "3.39", bbb)]);

        after.Mount.AbsolutePath.ShouldEndWith("usb-0403-6001-BBB", Case.Insensitive);
        DeviceFollowNotes.For(before, after).ShouldHaveSingleItem()
            .ShouldBe($"Skywatcher AZ-GTi (FW 3.39): followed to {after.Mount.DeviceKey} on COM5 (was {before.Mount.DeviceKey})");
    }
}
