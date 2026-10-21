# USB.Lib: the USB tree, its events and its transfers, as a sibling repo (plan)

**Status: NOT STARTED (written 2026-10-22, raised by the owner).** A `SharpAstro/USB.Lib` sibling that does for USB
what `Serial.Lib` did for serial ports (`serial-lib.md`): one place that knows the machine's USB devices, where each one
hangs, when each one comes and goes, and how to talk to one directly, behaving the same way on every call (deadlines,
cancellation, typed failures). TianWen and FC.SDK depend on it.

## Why: what the SV605CC bench sessions needed and did not have

Every fact that moved the SV605CC dropout investigation (#1749) on came from USB, and every one was read by hand or by
a throwaway script:

- **Where a device hangs.** That the SV605CC sat behind a VIA VL817 hub (`2109:0817` / `2817`), the same chip in the
  SVBONY SV241 and the Gemini Advanced 3, was read off Device Manager's parent chain. That the Gemini focuser on COM7
  is one hub further down than the box's own controller on COM6 (behind a WCH `1a86:8091`) was read the same way.
- **When a device came and went.** A whole hub resetting at 07:00:36, and a hub later stuck so that replugging the
  camera registered nothing, showed only in Windows' per-device arrival times, polled every 30 s (#1745).
- **What a device draws, and from where.** On 2026-10-22 the SV605CC's cooler drew nothing from the box's 12 V up to
  62 % and then 1.6 A at once, while a QHY178M on the same box showed its whole ramp: the SV605CC very likely runs its
  cooler from USB power below some load, which no 12 V meter can see. What a device declares it may take from VBUS
  (its configuration's `bMaxPower`) and what the port gives it is USB's to say.

And a power box with USB ports is half a USB hub: TianWen can only draw the rig's wiring (power-boxes P6, #1734) if it
knows which of the box's ports each device is on, and only a box that switches its USB ports (the Pegasus USB Control
Hub, power-boxes P9) can be told to cut one device's USB power, if TianWen knows which port that is.

## The libusb binding it takes over

FC.SDK references `LibUsbDotNet` 2.2.75 (`FC.SDK/src/FC.SDK/Transport/UsbPtpTransport.cs`, the PTP-over-USB transport
for a Canon body), and TianWen's web build carries it only to force it through the interpreter
(`TianWen.UI.Web.csproj`). That package is the only USB code in the org; USB.Lib replaces it with a binding of its own
(`LibraryImport`, AOT-clean, as every other SharpAstro native binding is), so FC.SDK's transport sits on USB.Lib and the
package goes.

## U1: the device tree

Tracked by #1754. Every USB device present, as a tree of hubs and ports from each host controller down: VID/PID, the
names Windows or the kernel gives it, the serial when there is one, its speed, its configuration's `bMaxPower`, its
instance path and location path (the hub and the port number), and what else it is (a COM port, a camera of a known
vendor's SDK, a hub's own billboard device). A hub is named by its chip where its IDs say so. Windows: CfgMgr32 /
SetupAPI (the parent chain and location paths), with the descriptors read through the hub driver; Linux: sysfs.

TianWen uses it to say, in a device's description and in the node's log, where it hangs ("behind the VL817 in the
Gemini box, port 2"), and to group devices by the box they hang off.

## U2: arrivals and removals

Tracked by #1745 (moved here from `serial-lib.md`'s open question 3). Every arrival and removal while the node runs,
from the platform's notifications (Windows `CM_Register_Notification` with no window, Linux udev netlink), with the
arrival and removal times Windows keeps (`DEVPKEY_Device_LastArrivalDate` / `LastRemovalDate`) for anything missed. A hub
whose devices all leave and return together is ONE hub reset. Serial.Lib's port enumeration and discovery react to the
same events. What TianWen says with them (the run's notes, beside a lost frame) is #1745's.

## U3: transfers, and FC.SDK on them

Tracked by #1755. Open a device by VID/PID (or by its place in U1's tree), claim an interface, and run control, bulk and
interrupt transfers with a deadline and a token, failing with one typed exception family (timeout carrying what did
arrive, stalled, removed, busy, not found), as Serial.Lib's `SerialException` does. Windows through WinUSB where a
device is bound to it, libusb elsewhere. Then FC.SDK's `UsbPtpTransport` moves onto it and `LibUsbDotNet` leaves FC.SDK
and TianWen's web build.

## U4: the wiring, read off the tree

Tracked under power-boxes P6 (#1734), which owns the wiring diagram. U1 fills its DATA half on its own: a device behind
a power box's hub is on that box, and on the port U1 names, so the diagram draws those lines without the user drawing
them, and the user names a port once (a box that names its own ports, as Pegasus's do, gives the names). The POWER half
(which 12 V output feeds which device) no USB tree can see; the user still draws those.

## Repository shape

As Serial.Lib: `SharpAstro/USB.Lib`, package `SharpAstro.Usb`, the org's CI and versioning pattern
(`../.github/docs/dotnet-ci-pattern.md`), the reliability tests living with the code (a real hub's tree on the bench;
the transfer path against a loopback where one can be made), and TianWen and FC.SDK taking it through `UseLocalSiblings`
and the release dance, never a local feed. The descriptors and the platform calls are its own; nothing in it knows an
astronomy device.

## Open questions

1. **What a port gives, not only what a device asks.** `bMaxPower` is a declaration. Whether a hub reports per-port
   current at all, and whether Windows exposes it, is to be measured; a USB power meter in the cable is the ground
   truth until then (#1749).
2. **WinUSB or libusb on Windows.** WinUSB needs the device bound to it, which a vendor driver (a camera's) prevents;
   libusb on Windows has the same limit. U3 serves devices TianWen talks to directly (a Canon body), not ones a vendor
   SDK owns; U1 and U2 need neither.
