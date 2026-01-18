using ImageMagick;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;

namespace TianWen.Lib.CLI;

internal class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    ICombinedDeviceManager deviceManager,
    IDeviceUriRegistry deviceUriRegistry
) : IConsoleHost
{
    private HashSet<TerminalCapability>? _deviceCapabilities;
    private int? _consoleWidthPx;
    private int? _consoleHeightPx;
    private int? _consoleWidthChars;
    private readonly ConcurrentDictionary<DeviceType, bool> _discoveryRanForDevice = [];

    public IDeviceUriRegistry DeviceUriRegistry { get; } = deviceUriRegistry;

    public IHostApplicationLifetime ApplicationLifetime { get; } = applicationLifetime;

    public IExternal External { get; } = external;

    public async Task<bool> HasSixelSupportAsync()
    {
        if (_deviceCapabilities is null)
        {
            var response = await GetControlSequenceResponse("\e[0c");

            _deviceCapabilities = [.. response
                    .TrimStart('\e', '[', '?')
                    .TrimEnd('c')
                    .Split(';')
                    .Select((s) => (TerminalCapability) int.Parse(s))
            ];
        }

        return _deviceCapabilities.Contains(TerminalCapability.Sixel);
    }

    private async ValueTask<string> GetControlSequenceResponse(string sequence)
    {
        const int maxTries = 10;

        var response = new StringBuilder();
        Console.WriteLine(sequence);

        int tries = 0;
        while (!Console.KeyAvailable && tries < maxTries)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        if (!Console.KeyAvailable)
        {
            External.AppLogger.LogDebug("Failed to read control sequence response for {Sequence} after {MaxTries}", sequence, maxTries);
        }

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            response.Append(key.KeyChar);
        }

        return response.ToString();
    }

    private async Task<(int WidthPx, int HeightPx)?> TryGetConsolePixelSizeAsync()
    {
        if (!await HasSixelSupportAsync())
        {
            return null;
        }

        if (!_consoleWidthPx.HasValue || !_consoleHeightPx.HasValue || !_consoleWidthChars.HasValue || _consoleWidthPx != Console.WindowWidth)
        {
            var response = await GetControlSequenceResponse("\e[14t");
            // Response is of the form ESC [ 4 ; height ; width t
            var parts = response.TrimStart('\e', '[').TrimEnd('t').Split(';');
            if (parts.Length == 3 && parts[0] == "4" &&
                int.TryParse(parts[1], out var heightPx) &&
                int.TryParse(parts[2], out var widthPx))
            {
                _consoleWidthPx = widthPx;
                _consoleHeightPx = heightPx;
                _consoleWidthChars = Console.WindowWidth;
                return (widthPx, heightPx);
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }
    }

    public async ValueTask RenderImageAsync(IMagickImage<float> image)
    {
        IConsoleImageRenderer renderer;
        Percentage? widthScale;
        if (await HasSixelSupportAsync())
        {
            renderer = new SixelRenderer();

            var pixelSize = await TryGetConsolePixelSizeAsync();
            widthScale = pixelSize.HasValue && image.Width > pixelSize.Value.WidthPx
                ? new Percentage(100d * ((double)image.Width / pixelSize.Value.WidthPx))
                : null;
        }
        else
        {
            renderer = new AsciiBlockRender();
            widthScale = null; // ASCII always tries to use full width available
        }

        Console.Write(renderer.Render(image, widthScale));
    }

    public async Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken)
    {
        TimeSpan discoveryTimeout;
#if DEBUG
        discoveryTimeout = TimeSpan.FromMinutes(15);
#else
        discoveryTimeout = TimeSpan.FromSeconds(25);
#endif
        using var cts = new CancellationTokenSource(discoveryTimeout, External.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        await deviceManager.CheckSupportAsync(linked.Token);

        if (options.HasFlag(DeviceDiscoveryOption.Force) || deviceManager.RegisteredDeviceTypes.Any(t => !_discoveryRanForDevice.TryGetValue(t, out var ran) || !ran))
        {
            await deviceManager.DiscoverAsync(linked.Token);

            foreach (var type in deviceManager.RegisteredDeviceTypes)
            {
                _discoveryRanForDevice[type] = true;
            }
        }

        var includeFake = options.HasFlag(DeviceDiscoveryOption.IncludeFake);

        return [.. deviceManager
            .RegisteredDeviceTypes
            .SelectMany(deviceManager.RegisteredDevices)
            .Where(d => includeFake || d is not FakeDevice)
            .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)
        ];
    }

    public async Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase
    {
        TimeSpan discoveryTimeout;
#if DEBUG
        discoveryTimeout = TimeSpan.FromMinutes(15);
#else
        discoveryTimeout = TimeSpan.FromSeconds(25);
#endif
        using var cts = new CancellationTokenSource(discoveryTimeout, External.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        if (await deviceManager.CheckSupportAsync(linked.Token) && (
            options.HasFlag(DeviceDiscoveryOption.Force) || !_discoveryRanForDevice.TryGetValue(deviceType, out var ran) || !ran)
        )
        {
            await deviceManager.DiscoverOnlyDeviceType(deviceType, linked.Token);
        }

        var includeFake = options.HasFlag(DeviceDiscoveryOption.IncludeFake);

        return [.. deviceManager
            .RegisteredDevices(deviceType)
            .OfType<TDevice>()
            .Where(d => includeFake || d is not FakeDevice)
            .OrderBy(d => d.DisplayName)
        ];
    }
}