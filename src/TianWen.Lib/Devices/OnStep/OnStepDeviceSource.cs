using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.OnStep;

/// <summary>
/// Discovers OnStep / OnStepX mounts via two transports:
/// <list type="bullet">
///   <item><description>Serial: enumerates COM/tty ports and probes with <c>:GVP#</c> + <c>:GVN#</c></description></item>
///   <item><description>WiFi: mDNS scan for <c>_telescope._tcp.local</c> then TCP probe on port 9999</description></item>
/// </list>
/// In both cases the probe matches the OnStep product-name signature (regex <c>^On[-]?Step</c>).
/// Stable device IDs derive from a UUID stashed in an unused site-name slot for serial mounts,
/// or from the IP+MAC for WiFi mounts.
/// </summary>
internal partial class OnStepDeviceSource(IExternal external, ILogger<OnStepDeviceSource> logger, ITimeProvider timeProvider, IPinnedSerialPortsProvider pinnedPortsProvider) : IDeviceSource<OnStepDevice>
{
    private Dictionary<DeviceType, List<OnStepDevice>>? _cachedDevices;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    private static readonly Regex SupportedProductsRegex = GenSupportedProductsRegex();
    private static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();

    // mDNS wire constants — same multicast group as Canon, different service name.
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private static readonly TimeSpan MdnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pre-built DNS PTR query for <c>_telescope._tcp.local</c> — the service the
    /// OnStep ESP32 SmartHand Controller and most other LX200-over-WiFi bridges advertise.
    /// 12-byte header + QNAME + QTYPE(PTR) + QCLASS(IN+QU bit).
    /// </summary>
    private static readonly byte[] TelescopeMdnsQuery =
    [
        // DNS header: ID=0, Flags=0, QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // QNAME: _telescope._tcp.local
        0x0A, (byte)'_', (byte)'t', (byte)'e', (byte)'l', (byte)'e', (byte)'s', (byte)'c', (byte)'o', (byte)'p', (byte)'e',
        0x04, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
        0x05, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l',
        0x00, // root label
        // QTYPE = PTR (12), QCLASS = IN with QU bit (0x8001)
        0x00, 0x0C, 0x80, 0x01
    ];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<OnStepDevice>>();

        // Phase 1: serial-port scan
        try
        {
            using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);
            var ports = pinnedPortsProvider.FilterUnpinned(external.EnumerateAvailableSerialPorts(resourceLock), logger);

            foreach (var portName in ports)
            {
                try
                {
                    await QueryPortAsync(devices, portName, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to query OnStep serial candidate {PortName}", portName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OnStep serial enumeration failed");
        }

        // Phase 2: WiFi via mDNS
        try
        {
            var mdnsHits = await ScanTelescopeMdnsAsync(MdnsTimeout, cancellationToken);
            foreach (var (instanceName, ipAddr) in mdnsHits)
            {
                try
                {
                    await QueryWifiHostAsync(devices, instanceName, ipAddr, OnStepDevice.DefaultTcpPort, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "OnStep WiFi probe failed for {Host}", ipAddr);
                }
            }
            logger.LogDebug("OnStep mDNS scan found {Count} _telescope._tcp.local responder(s)", mdnsHits.Count);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "OnStep mDNS WiFi discovery failed");
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
    }

    private async Task QueryPortAsync(Dictionary<DeviceType, List<OnStepDevice>> devices, string portName, CancellationToken cancellationToken)
    {
        using var probeScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["Port"] = portName,
            ["Baud"] = 9600,
            ["Source"] = "OnStep",
        });

        var ioTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(250);
        using var cts = new CancellationTokenSource(ioTimeout, timeProvider.System);
        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token;

        using var serialDevice = await external.OpenSerialDeviceAsync(portName, 9600, Encoding.ASCII, linkedToken);

        var (productName, productNumber, siteNames, uuid) = await TryGetMountInfo(serialDevice, linkedToken)
            .WaitAsync(ioTimeout, timeProvider.System, cancellationToken);
        if (productName is not null && productNumber is not null && SupportedProductsRegex.IsMatch(productName))
        {
            List<OnStepDevice> deviceList;
            if (devices.TryGetValue(DeviceType.Mount, out var existingMounts))
            {
                deviceList = existingMounts;
            }
            else
            {
                devices[DeviceType.Mount] = deviceList = [];
            }

            var portNameWithoutProtoPrefix = ISerialConnection.RemoveProtoPrrefix(portName);
            string deviceId;
            if (uuid is { })
            {
                deviceId = string.Join('_', SafeName(productName), SafeName(productNumber), uuid);
            }
            else
            {
                deviceId = string.Join('_',
                    SafeName(productName),
                    SafeName(productNumber),
                    SafeName(string.Join(',', siteNames)),
                    deviceList.Count + 1,
                    SafeName(portNameWithoutProtoPrefix)
                );
            }

            var device = new OnStepDevice(DeviceType.Mount, deviceId, $"{productName} ({productNumber}) on {portNameWithoutProtoPrefix}", portName);
            deviceList.Add(device);
        }
    }

    private static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    private static async Task<(string? ProductName, string? ProductNumber, List<string> Sites, string? UUID)> TryGetMountInfo(ISerialConnection serialDevice, CancellationToken cancellationToken)
    {
        const string uuidPrefix = "TW@";
        const string unusedSiteName = "<AN UNUSED SITE>";
        const int maxSiteLength = 15;

        using var @lock = await serialDevice.WaitAsync(cancellationToken);

        var productName = await TryReadTerminatedAsync(":GVP#");

        if (productName?.TrimEnd() is not { Length: > 1})
        {
            return (null, null, [], null);
        }

        var productNumber = await TryReadTerminatedAsync(":GVN#");

        string? uuid = null;
        var sites = new List<string>();
        for (var siteNo = 4; siteNo >= 1; siteNo--)
        {
            var siteChar = (char)('M' + siteNo - 1);
            var siteName = await TryReadTerminatedAsync($":G{siteChar}#");

            var trimmed = siteName?.TrimEnd();
            if (trimmed is null || trimmed.Length is 0)
            {
                continue;
            }

            if (trimmed.Length is maxSiteLength && trimmed.StartsWith(uuidPrefix))
            {
                uuid ??= trimmed[uuidPrefix.Length..];
            }
            else if (trimmed is unusedSiteName && uuid is null)
            {
                using var random = ArrayPoolHelper.Rent<byte>((maxSiteLength - uuidPrefix.Length) * 3 / 4);
                Random.Shared.NextBytes(random);
                var newUuid = Base64UrlSafe.Base64UrlEncode(random);

                if (await serialDevice.TryWriteAsync($":S{siteChar}TW@{newUuid}#", cancellationToken) &&
                    await serialDevice.TryReadExactlyAsync(1, cancellationToken) is "1")
                {
                    uuid = newUuid;
                }
            }
            else
            {
                sites.Add(trimmed);
            }
        }

        sites.Reverse();

        return (productName, productNumber, sites, uuid);

        async Task<string?> TryReadTerminatedAsync(string command)
        {
            if (await serialDevice.TryWriteAsync(command, cancellationToken))
            {
                return await serialDevice.TryReadTerminatedAsync(HashTerminator, cancellationToken);
            }

            return null;
        }
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public IEnumerable<OnStepDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_cachedDevices != null && _cachedDevices.TryGetValue(deviceType, out var devices))
        {
            return devices;
        }
        return [];
    }

    // OnStep and OnStepX both report product names beginning with "On-Step" or "OnStep".
    // OnStepX (the active fork hjd1964/OnStepX) typically returns "On-Step" via :GVP#.
    [GeneratedRegex("^On[-]?Step", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenSupportedProductsRegex();

    /// <summary>
    /// Probes a WiFi candidate (IP from mDNS) on TCP and registers it if it responds
    /// with an OnStep product name. Uses the shared <see cref="TryGetMountInfo"/> probe
    /// (same as the serial path) so the UUID-in-site-slot trick applies: the deviceId
    /// stays stable across DHCP-induced IP changes and across USB ↔ WiFi switches
    /// (the UUID lives in the mount's own memory, independent of transport).
    /// </summary>
    private async Task QueryWifiHostAsync(Dictionary<DeviceType, List<OnStepDevice>> devices, string instanceName, string ipAddr, int tcpPort, CancellationToken cancellationToken)
    {
        using var probeScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["Transport"] = "TCP",
            ["Host"] = $"{ipAddr}:{tcpPort}",
            ["Source"] = "OnStep",
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TcpProbeTimeout);

        ISerialConnection conn;
        try
        {
            conn = await TcpSerialConnection.CreateAsync(ipAddr, tcpPort, Encoding.ASCII, logger, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "OnStep TCP connect failed to {Host}:{Port}", ipAddr, tcpPort);
            return;
        }

        try
        {
            var (productName, productNumber, siteNames, uuid) = await TryGetMountInfo(conn, cts.Token);

            if (productName is null || productNumber is null || !SupportedProductsRegex.IsMatch(productName))
            {
                return;
            }

            var deviceList = devices.TryGetValue(DeviceType.Mount, out var existing) ? existing : (devices[DeviceType.Mount] = []);

            // Prefer the mount-resident UUID (same stable ID as the serial path). Fall back to
            // IP only if the mount doesn't expose 4 site slots (non-standard firmware).
            string deviceId;
            if (uuid is { Length: > 0 })
            {
                deviceId = string.Join('_', SafeName(productName), SafeName(productNumber), uuid);
            }
            else
            {
                deviceId = string.Join('_',
                    SafeName(productName),
                    SafeName(productNumber),
                    SafeName(string.Join(',', siteNames)),
                    deviceList.Count + 1,
                    "wifi",
                    SafeName(ipAddr)
                );
            }

            var displayName = instanceName.Length > 0
                ? $"{productName} ({productNumber}) [{instanceName} @ {ipAddr}]"
                : $"{productName} ({productNumber}) [WiFi @ {ipAddr}]";

            deviceList.Add(new OnStepDevice(DeviceType.Mount, deviceId, displayName, ipAddr, tcpPort));

            logger.LogDebug("Discovered OnStep WiFi mount: {Name} at {Ip}:{Port} (uuid={Uuid})", displayName, ipAddr, tcpPort, uuid ?? "<none>");
        }
        finally
        {
            conn.TryClose();
        }
    }

    /// <summary>
    /// Sends a DNS PTR query for <c>_telescope._tcp.local</c> and collects A-record
    /// responses. Returns (instance-name, ipAddr) pairs. Adapted from the Canon
    /// mDNS scanner — same wire format, different service name.
    /// </summary>
    private static async Task<IReadOnlyList<(string InstanceName, string IpAddr)>> ScanTelescopeMdnsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var results = new List<(string, string)>();

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(MdnsMulticast);
        }
        catch (SocketException)
        {
            // mDNS port already in use (Bonjour/Avahi running) — fall back to ephemeral port.
            // The query still goes out, but multicast responses won't reach us. Return empty.
            return results;
        }

        await udp.SendAsync(TelescopeMdnsQuery, new IPEndPoint(MdnsMulticast, MdnsPort), ct);

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
            // Expected — collection window elapsed
        }

        return results;
    }

    /// <summary>Parses a DNS response packet, extracting PTR (instance name) and A (IPv4) records.</summary>
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

        var offset = 12;
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        for (var i = 0; i < questionCount && offset < data.Length; i++)
        {
            SkipDnsName(data, ref offset);
            offset += 4; // QTYPE + QCLASS
        }

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
                    var suffixIdx = name.IndexOf("._telescope._tcp.local", StringComparison.OrdinalIgnoreCase);
                    instanceName = suffixIdx > 0 ? name[..suffixIdx] : name;
                    break;
                }

                case 1 when rdLength == 4: // A — IPv4
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

    /// <summary>Reads a DNS name (handles label compression pointers).</summary>
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

    /// <summary>Skips past a DNS name in the packet without parsing it.</summary>
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
}
