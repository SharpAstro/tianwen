using Console.Lib;
using DIR.Lib;
using System.CommandLine;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.View;

internal class ViewSubCommand(
    IConsoleHost consoleHost,
    ViewerState state,
    DocumentCache documentCache
)
{
    private readonly Argument<string> pathArg = new Argument<string>("path") { Description = "FITS file or directory to view" };

    public Command Build()
    {
        var viewCommand = new Command("view", "View a FITS image in the terminal")
        {
            Arguments = { pathArg }
        };
        viewCommand.SetAction(ViewActionAsync);

        return viewCommand;
    }

    internal async Task ViewActionAsync(ParseResult parseResult, CancellationToken ct)
    {
        var path = parseResult.GetRequiredValue(pathArg);
        await RunNonInteractiveAsync(path, ct);
    }

    private async Task EnsureTerminalInitAsync()
    {
        // InitAsync probes capabilities — safe to call multiple times (no-op after first)
        await consoleHost.Terminal.InitAsync();
    }

    internal async Task RunNonInteractiveAsync(string inputPath, CancellationToken ct)
    {
        await EnsureTerminalInitAsync();
        var terminal = consoleHost.Terminal;

        // Resolve file path
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
            consoleHost.WriteError($"No supported image found: {inputPath}");
            return;
        }

        // Load image
        var document = await documentCache.GetOrLoadAsync(filePath, state.DebayerAlgorithm, ct);
        if (document is null)
        {
            consoleHost.WriteError($"Failed to open: {filePath}");
            return;
        }

        // Apply default stretch for linear images
        state.StretchMode = document.IsPreStretched ? StretchMode.None : StretchMode.Unlinked;

        var termW = terminal.Size.Width;
        var termH = terminal.Size.Height;

        // Compute pixel dimensions: for Sixel use full terminal pixel size, for ASCII use cell grid
        var pixelW = terminal.HasSixelSupport ? (int)terminal.PixelSize.Width : termW;
        var pixelH = terminal.HasSixelSupport ? (int)terminal.PixelSize.Height : termH * 2;

        // Print metadata header to stderr
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
            using var ms = new MemoryStream();
            imageRenderer.EncodeSixel(ms);
            ms.Position = 0;
            ms.CopyTo(terminal.OutputStream);
            terminal.Flush();
        }
        else
        {
            var surface = imageRenderer.Surface;
            AsciiRenderer.Render(surface.Pixels, surface.Width, surface.Height, System.Console.Out);
        }

        // Print stretch info on stderr
        var stretchLabel = state.StretchMode switch
        {
            StretchMode.None => "None (pre-stretched)",
            StretchMode.Unlinked => "STF Unlinked",
            StretchMode.Linked => "STF Linked",
            StretchMode.Luma => "STF Luma",
            _ => state.StretchMode.ToString()
        };
        System.Console.Error.WriteLine($"Stretch: {stretchLabel}");
    }

}
