using Console.Lib;
using DIR.Lib;
using System.CommandLine;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.View;

internal class ViewSubCommand(
    IConsoleHost consoleHost,
    ViewerState state,
    DocumentCache documentCache,
    Option<bool> inlineOption
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
        var inline = parseResult.GetValue(inlineOption);

        if (inline)
        {
            await RunNonInteractiveAsync(path, ct);
        }
        else
        {
            await RunInteractiveAsync(path, ct);
        }
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

    internal async Task RunInteractiveAsync(string inputPath, CancellationToken ct)
    {
        await EnsureTerminalInitAsync();
        var terminal = consoleHost.Terminal;

        // Resolve file path and scan folder
        string? filePath = null;
        string? folderPath = null;

        if (File.Exists(inputPath))
        {
            filePath = Path.GetFullPath(inputPath);
            folderPath = Path.GetDirectoryName(filePath);
        }
        else if (Directory.Exists(inputPath))
        {
            folderPath = Path.GetFullPath(inputPath);
        }

        if (folderPath is not null)
        {
            ViewerActions.ScanFolder(state, folderPath, filePath is not null ? Path.GetFileName(filePath) : null);
        }

        if (filePath is null && state.ImageFileNames.Count > 0 && folderPath is not null)
        {
            filePath = Path.Combine(folderPath, state.ImageFileNames[0]);
            state.SelectedFileIndex = 0;
        }

        if (filePath is null)
        {
            consoleHost.WriteError($"No supported image found: {inputPath}");
            return;
        }

        // Enter alternate screen
        terminal.EnterAlternateScreen();

        try
        {
            await RunInteractiveLoopAsync(filePath, folderPath, ct);
        }
        finally
        {
            // DisposeAsync on VirtualTerminal will leave alternate screen
        }
    }

    private async Task RunInteractiveLoopAsync(string initialFilePath, string? folderPath, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        AstroImageDocument? document = null;
        var needsReload = true;
        var needsRedraw = true;
        var currentFilePath = initialFilePath;

        // Build panel layout
        var panel = new Panel(terminal);
        var topVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var leftVp = panel.Dock(DockStyle.Left, 28);
        var fillVp = panel.Fill();

        var topBar = new TextBar(topVp);
        var statusBar = new TextBar(bottomVp);
        var fileList = new ScrollableList<FileListItem>(leftVp);

        // Build canvas renderer — use pixel size from fill viewport
        var canvasPixelSize = fillVp.PixelSize;
        var canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
        var canvas = new Canvas<RgbaImage>(fillVp, canvasRenderer);

        panel.Add(topBar).Add(statusBar).Add(fileList).Add(canvas);

        var imageRenderer = new ConsoleImageRenderer((int)canvasPixelSize.Width, (int)canvasPixelSize.Height);

        while (!ct.IsCancellationRequested)
        {
            // Load image if needed
            if (needsReload)
            {
                needsReload = false;
                document = await documentCache.GetOrLoadAsync(currentFilePath, state.DebayerAlgorithm, ct);
                if (document is not null)
                {
                    state.StretchMode = document.IsPreStretched ? StretchMode.None : StretchMode.Unlinked;
                }
                needsRedraw = true;
            }

            // Render
            if (needsRedraw && document is not null)
            {
                needsRedraw = false;

                // Render image to canvas
                imageRenderer.RenderImage(document, state);
                // Copy pixels to canvas surface
                var src = imageRenderer.Surface;
                var dst = (RgbaImage)canvasRenderer.Surface;
                if (src.Width == dst.Width && src.Height == dst.Height)
                {
                    Buffer.BlockCopy(src.Pixels, 0, dst.Pixels, 0, src.Pixels.Length);
                }

                // Update file list
                var items = new FileListItem[state.ImageFileNames.Count];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = new FileListItem(state.ImageFileNames[i], i == state.SelectedFileIndex);
                }
                fileList.Items(items).ScrollTo(Math.Max(0, state.SelectedFileIndex - fileList.VisibleRows / 2));

                // Update bars
                var stretchLabel = state.StretchMode switch
                {
                    StretchMode.None => "None",
                    StretchMode.Unlinked => "STF",
                    StretchMode.Linked => "STF Linked",
                    StretchMode.Luma => "STF Luma",
                    _ => state.StretchMode.ToString()
                };
                var meta = document.UnstretchedImage.ImageMeta;
                topBar.Text($" {Path.GetFileName(currentFilePath)}  |  {document.UnstretchedImage.Width}x{document.UnstretchedImage.Height}  |  Stretch: {stretchLabel}");
                topBar.RightText($"{meta.ObjectName ?? ""} ");

                statusBar.Text(" T:stretch S:stars C:chan D:debay ↑↓:files Q:quit");
                statusBar.RightText($"Stars: {document.Stars?.Count ?? 0}  HFR: {document.AverageHFR:F2} ");

                panel.RenderAll();
            }

            // Wait for input
            if (!terminal.HasInput())
            {
                await Task.Delay(16, ct);
                continue;
            }

            var evt = terminal.TryReadInput();

            // Handle resize
            if (panel.Recompute())
            {
                canvasPixelSize = fillVp.PixelSize;
                canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
                // Recreate canvas widget with new renderer
                panel = new Panel(terminal);
                topVp = panel.Dock(DockStyle.Top, 1);
                bottomVp = panel.Dock(DockStyle.Bottom, 1);
                leftVp = panel.Dock(DockStyle.Left, 28);
                fillVp = panel.Fill();
                topBar = new TextBar(topVp);
                statusBar = new TextBar(bottomVp);
                fileList = new ScrollableList<FileListItem>(leftVp);
                canvas = new Canvas<RgbaImage>(fillVp, canvasRenderer);
                panel.Add(topBar).Add(statusBar).Add(fileList).Add(canvas);
                imageRenderer = new ConsoleImageRenderer((int)canvasPixelSize.Width, (int)canvasPixelSize.Height);
                needsRedraw = true;
                continue;
            }

            // Handle keyboard
            switch (evt.Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape:
                    return;

                case ConsoleKey.T:
                    ViewerActions.ToggleStretch(state);
                    needsRedraw = true;
                    break;

                case ConsoleKey.S:
                    state.ShowStarOverlay = !state.ShowStarOverlay;
                    needsRedraw = true;
                    break;

                case ConsoleKey.C:
                    if (document is not null)
                    {
                        ViewerActions.CycleChannelView(state, document.UnstretchedImage.ChannelCount);
                        needsRedraw = true;
                    }
                    break;

                case ConsoleKey.D:
                    ViewerActions.CycleDebayerAlgorithm(state);
                    needsRedraw = true;
                    break;

                case ConsoleKey.UpArrow:
                    if (state.SelectedFileIndex > 0 && folderPath is not null)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                        currentFilePath = Path.Combine(folderPath, state.ImageFileNames[state.SelectedFileIndex]);
                        needsReload = true;
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (state.SelectedFileIndex < state.ImageFileNames.Count - 1 && folderPath is not null)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
                        currentFilePath = Path.Combine(folderPath, state.ImageFileNames[state.SelectedFileIndex]);
                        needsReload = true;
                    }
                    break;

                case ConsoleKey.OemPlus or ConsoleKey.Add:
                    ViewerActions.CycleStretchPreset(state);
                    needsRedraw = true;
                    break;

                case ConsoleKey.OemMinus or ConsoleKey.Subtract:
                    ViewerActions.CycleStretchPreset(state, reverse: true);
                    needsRedraw = true;
                    break;
            }
        }
    }
}
