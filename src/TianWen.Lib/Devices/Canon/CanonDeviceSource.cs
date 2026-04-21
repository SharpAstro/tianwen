using FC.SDK;
using FC.SDK.Transport;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Device source for Canon DSLR cameras.
/// Discovers cameras via WPD (Windows), USB (LibUsbDotNet), and WiFi (mDNS + PTP/IP).
/// </summary>
internal sealed class CanonDeviceSource(ILogger<CanonDeviceSource> logger) : IDeviceSource<CanonDevice>
{
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private static readonly TimeSpan MdnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Pre-built DNS PTR query for <c>_ptp._tcp.local</c>.
    /// 12-byte header + QNAME (_ptp._tcp.local in DNS label encoding) + QTYPE(PTR) + QCLASS(IN+QU).
    /// </summary>
    private static readonly byte[] PtpTcpMdnsQuery =
    [
        // DNS header: ID=0, Flags=0, QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // QNAME: _ptp._tcp.local
        0x04, (byte)'_', (byte)'p', (byte)'t', (byte)'p',
        0x04, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
        0x05, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l',
        0x00, // root label
        // QTYPE = PTR (12), QCLASS = IN with QU bit (0x8001)
        0x00, 0x0C, 0x80, 0x01
    ];

    private readonly List<CanonDevice> _cameras = [];
    private bool _supported;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Probe if LibUsbDotNet is available by attempting enumeration
            _ = CanonCamera.EnumerateUsbCameras();
            _supported = true;
        }
        catch
        {
            // LibUsbDotNet not installed or WinUSB driver not available — USB discovery disabled
            // WiFi discovery still works (mDNS + TCP)
            _supported = true; // Always supported — WiFi doesn't need LibUsbDotNet
        }
        return ValueTask.FromResult(_supported);
    }

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        _cameras.Clear();

        // Phase 1: WPD cameras (Windows only — uses stock MTP driver, no Zadig needed)
        if (OperatingSystem.IsWindows())
        {
            DiscoverWpdCameras();
        }

        // Phase 2: USB cameras (LibUsbDotNet — needs WinUSB driver on Windows)
        try
        {
            var usbCount = 0;
            foreach (var usb in CanonCamera.EnumerateUsbCameras())
            {
                usbCount++;
                var deviceId = !string.IsNullOrEmpty(usb.SerialNumber) ? usb.SerialNumber
                    : !string.IsNullOrEmpty(usb.DevicePath) ? usb.DevicePath
                    : $"{usb.VendorId:X4}:{usb.ProductId:X4}";

                var displayName = !string.IsNullOrEmpty(usb.Product) ? usb.Product : "Canon DSLR";
                var uri = new Uri($"{DeviceType.Camera}://{nameof(CanonDevice)}/{Uri.EscapeDataString(deviceId)}" +
                    $"?{DeviceQueryKey.Port.Key}=usb#{Uri.EscapeDataString(displayName)}");
                _cameras.Add(new CanonDevice(uri) );

                logger.LogDebug("Discovered Canon USB camera: {Name} ({Id})", displayName, deviceId);
            }
            logger.LogDebug("Canon USB enumeration found {Count} camera(s)", usbCount);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Canon USB enumeration failed (LibUsbDotNet not available?)");
        }

        // Phase 3: WiFi cameras via mDNS
        try
        {
            var mdnsResults = await ScanPtpTcpMdnsAsync(MdnsTimeout, cancellationToken);
            foreach (var (instanceName, ipAddr) in mdnsResults)
            {
                // Quick PTP/IP handshake to get stable responder GUID
                var guid = await GetWifiGuidAsync(ipAddr, HandshakeTimeout, cancellationToken);
                var deviceId = guid ?? ipAddr; // fall back to IP if handshake fails
                var displayName = instanceName.Length > 0 ? instanceName : "Canon WiFi";

                var uri = new Uri($"{DeviceType.Camera}://{nameof(CanonDevice)}/{Uri.EscapeDataString(deviceId)}" +
                    $"?{DeviceQueryKey.Port.Key}=wifi&{DeviceQueryKey.Host.Key}={Uri.EscapeDataString(ipAddr)}" +
                    $"#{Uri.EscapeDataString(displayName)}");
                _cameras.Add(new CanonDevice(uri) );

                logger.LogDebug("Discovered Canon WiFi camera: {Name} at {Ip} (id={Id})", displayName, ipAddr, deviceId);
            }
            logger.LogDebug("Canon mDNS scan found {Count} WiFi camera(s)", mdnsResults.Count);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Canon mDNS WiFi discovery failed");
        }
    }

    [SupportedOSPlatform("windows")]
    private void DiscoverWpdCameras()
    {
        try
        {
            var wpdCount = 0;
            foreach (var (wpdDeviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
            {
                wpdCount++;
                var uri = new Uri($"{DeviceType.Camera}://{nameof(CanonDevice)}/{Uri.EscapeDataString(wpdDeviceId)}" +
                    $"?{DeviceQueryKey.Port.Key}=wpd#{Uri.EscapeDataString(friendlyName)}");
                _cameras.Add(new CanonDevice(uri) );

                logger.LogDebug("Discovered Canon WPD camera: {Name} ({Id})", friendlyName, wpdDeviceId);
            }
            logger.LogDebug("Canon WPD enumeration found {Count} camera(s)", wpdCount);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Canon WPD enumeration failed");
        }
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = [DeviceType.Camera];

    public IEnumerable<CanonDevice> RegisteredDevices(DeviceType deviceType)
        => deviceType is DeviceType.Camera ? _cameras : [];

    /// <summary>
    /// Sends a DNS PTR query for <c>_ptp._tcp.local</c> over mDNS multicast and collects responses.
    /// Returns discovered PTP camera instance names and IP addresses.
    /// </summary>
    private static async Task<IReadOnlyList<(string InstanceName, string IpAddr)>> ScanPtpTcpMdnsAsync(
        TimeSpan timeout, CancellationToken ct)
    {
        var results = new List<(string, string)>();

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        udp.JoinMulticastGroup(MdnsMulticast);

        // Send the query
        await udp.SendAsync(PtpTcpMdnsQuery, new IPEndPoint(MdnsMulticast, MdnsPort), ct);

        // Collect responses until timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var seen = new HashSet<string>();
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var parsed = ParseMdnsResponse(result.Buffer);
                foreach (var entry in parsed)
                {
                    if (seen.Add(entry.IpAddr))
                    {
                        results.Add(entry);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — timeout elapsed
        }

        return results;
    }

    /// <summary>
    /// Parses a DNS response packet for PTR, SRV, and A records to extract
    /// PTP camera instance names and IP addresses.
    /// </summary>
    private static List<(string InstanceName, string IpAddr)> ParseMdnsResponse(byte[] data)
    {
        var results = new List<(string, string)>();
        if (data.Length < 12)
        {
            return results;
        }

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        var totalRecords = answerCount + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)) + additionalCount;

        // Skip question section
        var offset = 12;
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        for (var i = 0; i < questionCount && offset < data.Length; i++)
        {
            SkipDnsName(data, ref offset);
            offset += 4; // QTYPE + QCLASS
        }

        // Parse answer + authority + additional sections
        string? instanceName = null;
        string? ipAddr = null;

        for (var i = 0; i < totalRecords && offset < data.Length; i++)
        {
            SkipDnsName(data, ref offset);
            if (offset + 10 > data.Length)
            {
                break;
            }

            var rType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 8; // TYPE(2) + CLASS(2) + TTL(4)
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 2;

            if (offset + rdLength > data.Length)
            {
                break;
            }

            switch (rType)
            {
                case 12: // PTR — instance name
                {
                    var nameOffset = offset;
                    var name = ReadDnsName(data, ref nameOffset);
                    // Strip the service suffix to get just the instance name
                    var suffixIdx = name.IndexOf("._ptp._tcp.local", StringComparison.OrdinalIgnoreCase);
                    instanceName = suffixIdx > 0 ? name[..suffixIdx] : name;
                    break;
                }

                case 1 when rdLength == 4: // A record — IPv4 address
                {
                    ipAddr = new IPAddress(data.AsSpan(offset, 4)).ToString();
                    break;
                }
            }

            offset += rdLength;
        }

        if (ipAddr is not null)
        {
            results.Add((instanceName ?? "", ipAddr));
        }

        return results;
    }

    /// <summary>Reads a DNS name (handling label compression pointers).</summary>
    private static string ReadDnsName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder(64);
        var maxJumps = 10;
        var jumped = false;
        var savedOffset = 0;

        while (offset < data.Length && maxJumps-- > 0)
        {
            var len = data[offset];
            if (len == 0)
            {
                if (!jumped) offset++;
                break;
            }

            // Compression pointer (top 2 bits = 11)
            if ((len & 0xC0) == 0xC0)
            {
                if (!jumped) savedOffset = offset + 2;
                jumped = true;
                offset = ((len & 0x3F) << 8) | data[offset + 1];
                continue;
            }

            offset++;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }

        if (jumped) offset = savedOffset;
        return sb.ToString();
    }

    /// <summary>Skips a DNS name in the packet (for advancing past fields we don't need to read).</summary>
    private static void SkipDnsName(byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0) { offset++; break; }
            if ((len & 0xC0) == 0xC0) { offset += 2; break; }
            offset += 1 + len;
        }
    }

    /// <summary>
    /// Connects to a PTP/IP camera briefly to read the stable responder GUID, then disconnects.
    /// Returns null on any error (timeout, connection refused, etc.).
    /// </summary>
    private static async Task<string?> GetWifiGuidAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await using var camera = CanonCamera.ConnectWifi(host, "TianWen.Discovery");
            var result = await camera.OpenSessionAsync(cts.Token);
            if (result is not FC.SDK.Canon.EdsError.OK)
            {
                return null;
            }

            var deviceId = camera.DeviceId;
            await camera.CloseSessionAsync(cts.Token);
            return deviceId;
        }
        catch
        {
            return null;
        }
    }
}
