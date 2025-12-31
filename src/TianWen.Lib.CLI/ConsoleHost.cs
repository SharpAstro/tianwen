using ImageMagick;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    ICombinedDeviceManager deviceManager,
    IDeviceUriRegistry deviceUriRegistry
) : IConsoleHost
{
    private HashSet<int>? _deviceCapabilities;
    private int? _consoleWidthPx;
    private int? _consoleHeightPx;
    private int? _consoleWidthChars;

    public IDeviceUriRegistry DeviceUriRegistry => deviceUriRegistry;

    public bool HasSixelSupport
    {
        get
        {
            if (_deviceCapabilities is null)
            {
                var response = new StringBuilder();
                Console.WriteLine("\e[0c");
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    response.Append(key.KeyChar);
                }

                _deviceCapabilities = [.. response.ToString()
                    .TrimStart('\e', '[', '?')
                    .TrimEnd('c')
                    .Split(';')
                    .Select(int.Parse)
                ];
            }

            return _deviceCapabilities.Contains(4);
        }
    }

    private bool TryGetConsolePixelSize(out int widthPx, out int heightPx)
    {
        if (!HasSixelSupport)
        {
            widthPx = -1;
            heightPx = -1;
            return false;
        }

        if (!_consoleWidthPx.HasValue || !_consoleHeightPx.HasValue || !_consoleWidthChars.HasValue || _consoleWidthPx != Console.WindowWidth)
        {
            var response = new StringBuilder();
            Console.WriteLine("\e[14t");
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                response.Append(key.KeyChar);
            }
            var respStr = response.ToString();
            // Response is of the form ESC [ 4 ; height ; width t
            var parts = respStr.TrimStart('\e', '[').TrimEnd('t').Split(';');
            if (parts.Length == 3 && parts[0] == "4" &&
                int.TryParse(parts[1], out heightPx) &&
                int.TryParse(parts[2], out widthPx))
            {
                _consoleWidthPx = widthPx;
                _consoleHeightPx = heightPx;
                _consoleWidthChars = Console.WindowWidth;
                return true;
            }
            else
            {
                widthPx = -1;
                heightPx = -1;
                return false;
            }
        }
        else
        {
            widthPx = _consoleWidthPx.Value;
            heightPx = _consoleHeightPx.Value;
            return true;
        }
    }

    public void RenderImage(IMagickImage<float> image)
    {
        IConsoleImageRenderer renderer;
        Percentage? widthScale;
        if (HasSixelSupport && TryGetConsolePixelSize(out var widthPx, out _))
        {
            renderer = new SixelRenderer();
            widthScale = image.Width > widthPx ? new Percentage(100d * ((double)image.Width / widthPx)) : null;
        }
        else
        {
            renderer = new AsciiBlockRender();
            widthScale = null; // ASCII always tries to use full width available
        }

        Console.Write(renderer.Render(image, widthScale));
    }

    public ILogger Logger => external.AppLogger;

    public async Task<IReadOnlyCollection<Profile>> ListProfilesAsync()
    {
        TimeSpan discoveryTimeout;
#if DEBUG
        discoveryTimeout = TimeSpan.FromHours(1);
#else
        discoveryTimeout = TimeSpan.FromSeconds(25);
#endif

        using var cts = new CancellationTokenSource(discoveryTimeout, external.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, applicationLifetime.ApplicationStopping);

        if (await deviceManager.CheckSupportAsync(linked.Token))
        {
            await deviceManager.DiscoverOnlyDeviceType(DeviceType.Profile, linked.Token);
        }

        return [..deviceManager.RegisteredDevices(DeviceType.Profile).OfType<Profile>()];
    }
}