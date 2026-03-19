using Console.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;
using TianWen.UI.Console;

// DI setup
var services = new ServiceCollection();
services
    .AddFileLogging("FitsConsole")
    .AddFitsViewer()
    .AddExternal()
    .AddAstrometry();

var sp = services.BuildServiceProvider();
var state = sp.GetRequiredService<ViewerState>();
var logger = sp.GetRequiredService<IExternal>().AppLogger;

if (args.Length < 1)
{
    System.Console.Error.WriteLine("Usage: TianWen.UI.Console <fits-file-or-directory>");
    return 1;
}

var inputPath = args[0];
string? filePath = null;

if (File.Exists(inputPath))
{
    filePath = Path.GetFullPath(inputPath);
}
else if (Directory.Exists(inputPath))
{
    ViewerActions.ScanFolder(state, Path.GetFullPath(inputPath));
    if (state.ImageFileNames.Count > 0)
    {
        filePath = Path.Combine(Path.GetFullPath(inputPath), state.ImageFileNames[0]);
    }
}

if (filePath is null)
{
    System.Console.Error.WriteLine($"No supported image found: {inputPath}");
    return 1;
}

// Load image
var documentCache = new DocumentCache();
var document = await documentCache.GetOrLoadAsync(filePath, state.DebayerAlgorithm, CancellationToken.None);
if (document is null)
{
    System.Console.Error.WriteLine($"Failed to open: {filePath}");
    return 1;
}

// Apply default stretch for linear images
state.StretchMode = document.IsPreStretched ? StretchMode.None : StretchMode.Unlinked;

// Detect terminal capabilities
await using var terminal = new VirtualTerminal();
await terminal.InitAsync();

var termW = terminal.Size.Width;
var termH = terminal.Size.Height;

// Use terminal cell size to compute pixel dimensions
// Each cell is roughly 8x16 pixels, but with half-block chars we get 2 vertical pixels per row
var pixelW = termW;
var pixelH = termH * 2; // half-block doubles vertical resolution

logger.LogInformation("Terminal: {Width}x{Height} cells, Sixel={HasSixel}",
    termW, termH, terminal.HasSixelSupport);

// Print metadata header
var meta = document.UnstretchedImage.ImageMeta;
System.Console.Error.WriteLine($"File: {Path.GetFileName(filePath)}");
System.Console.Error.WriteLine($"Size: {document.UnstretchedImage.Width}x{document.UnstretchedImage.Height}x{document.UnstretchedImage.ChannelCount}ch");
if (!string.IsNullOrEmpty(meta.ObjectName))
{
    System.Console.Error.WriteLine($"Object: {meta.ObjectName}");
}

// Render image
var imageRenderer = new ConsoleImageRenderer(pixelW, pixelH);
imageRenderer.RenderImage(document, state);

if (terminal.HasSixelSupport)
{
    // Sixel output — high quality, supported by WezTerm/iTerm2/mintty
    using var ms = new MemoryStream();
    imageRenderer.EncodeSixel(ms);
    ms.Position = 0;
    ms.CopyTo(terminal.OutputStream);
    terminal.Flush();
}
else
{
    // ASCII fallback — Unicode half-block characters with 24-bit color
    var surface = imageRenderer.Surface;
    AsciiRenderer.Render(surface.Pixels, surface.Width, surface.Height, System.Console.Out);
}

// Print stretch info on stderr (doesn't interfere with image output)
var stretchLabel = state.StretchMode switch
{
    StretchMode.None => "None (pre-stretched)",
    StretchMode.Unlinked => "STF Unlinked",
    StretchMode.Linked => "STF Linked",
    StretchMode.Luma => "STF Luma",
    _ => state.StretchMode.ToString()
};
System.Console.Error.WriteLine($"Stretch: {stretchLabel}");

return 0;
