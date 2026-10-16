using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a recording's SER says about its rate, read back the one way for every rate probe (TIA2): frame count over the
/// file's own timestamp span, and each step between consecutive frames' timestamps longer than a stated bound. A frame is
/// stamped with the time its driver read it, so a step that long is time the driver read nothing.
/// </summary>
internal sealed record SerRateReadBack(int Frames, TimeSpan Span, double StepP50Ms, double StepP99Ms, TimeSpan MaxStep,
    ImmutableArray<(int AfterFrame, TimeSpan Step)> LongSteps, TimeSpan LongStepBound)
{
    /// <summary>The window the SER's header says it holds, and its depth in bits: what the recording was taken of.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public int PixelDepth { get; init; }

    /// <summary>Frame count over the timestamp span, as #1596's bar reads it.</summary>
    public double Fps => Span > TimeSpan.Zero ? Frames / Span.TotalSeconds : 0;

    /// <summary>The rate with the long steps' time taken out: what the camera delivered around a stall.</summary>
    public double FpsOutsideLongSteps
    {
        get
        {
            var outside = Span - LongSteps.Aggregate(TimeSpan.Zero, static (sum, step) => sum + step.Step);
            return outside > TimeSpan.Zero ? Frames / outside.TotalSeconds : 0;
        }
    }

    public static SerRateReadBack Read(string path, TimeSpan longStepBound)
    {
        using var ser = SerReader.Open(path);
        DateTimeOffset[] stamps = [.. ser.Timestamps];
        var steps = new double[Math.Max(0, stamps.Length - 1)];
        var longSteps = ImmutableArray.CreateBuilder<(int, TimeSpan)>();
        for (var i = 1; i < stamps.Length; i++)
        {
            var step = stamps[i] - stamps[i - 1];
            steps[i - 1] = step.TotalMilliseconds;
            if (step > longStepBound)
            {
                longSteps.Add((i - 1, step));
            }
        }
        Array.Sort(steps);
        double At(double p) => steps.Length == 0 ? double.NaN : steps[(int)Math.Round(p * (steps.Length - 1))];
        return new SerRateReadBack(ser.FrameCount, stamps.Length > 1 ? stamps[^1] - stamps[0] : TimeSpan.Zero, At(0.5), At(0.99),
            TimeSpan.FromMilliseconds(steps.Length == 0 ? 0 : steps[^1]), longSteps.ToImmutable(), longStepBound)
        {
            Width = ser.Width,
            Height = ser.Height,
            PixelDepth = ser.PixelDepthPerPlane,
        };
    }

    public override string ToString()
    {
        var longSteps = LongSteps.Length == 0
            ? "none"
            : string.Join("; ", LongSteps.Select(static s => $"{s.Step.TotalSeconds:F2} s after frame {s.AfterFrame}"))
                + $" ({FpsOutsideLongSteps:F1} fps outside them)";
        return $"SER {Frames} frames over {Span.TotalSeconds:F2} s = {Fps:F1} fps (frame count over its timestamp span), "
            + $"steps p50 {StepP50Ms:F2} / p99 {StepP99Ms:F2} / max {MaxStep.TotalMilliseconds:F1} ms, "
            + $"steps over {LongStepBound.TotalMilliseconds:F0} ms: {longSteps}";
    }
}

/// <summary>
/// The node rate gate's comparison with the same session's in-process rate (TIA2), out of the probe so its rules are tested: a
/// recording through the node must read within <see cref="WithinInProcess"/> of the in-process recordings made beside it.
/// </summary>
internal static class RateGate
{
    /// <summary>How far below the same session's in-process rate a gated recording may read.</summary>
    public const double WithinInProcess = 0.03;

    /// <summary>The bar, decided with the controller: at least this many frames a second off every gated recording's SER.</summary>
    public const double Bar = 190;

    /// <summary>
    /// What fails a recording the gate judges, in words, or nothing when it passes: under <see cref="Bar"/>, not within
    /// <see cref="WithinInProcess"/> of the same session's in-process rate (or no such rate), a frame the hand-off replaced (or
    /// no count of them), a frame the body or the disk dropped, a step over the SER's long-step bound.
    /// </summary>
    public static IReadOnlyList<string> Failures(SerRateReadBack ser, long? replaced, int bodyDropped, int diskDropped, double? baseline)
    {
        var failures = new List<string>();
        if (ser.Fps < Bar)
        {
            failures.Add($"{ser.Fps:F1} fps, under the bar of {Bar}");
        }
        if (!IsWithinInProcess(ser.Fps, baseline))
        {
            failures.Add(baseline is { } rate && double.IsFinite(rate)
                ? $"{ser.Fps:F1} fps, {ser.Fps / rate:P1} of the in-process {rate:F1}, not within {WithinInProcess:P0}"
                : "no same-session in-process rate to compare with");
        }
        if (replaced is not 0)
        {
            failures.Add(replaced is { } count ? $"{count} replaced by the hand-off" : "no count of the frames the hand-off replaced");
        }
        if (bodyDropped != 0)
        {
            failures.Add($"{bodyDropped} dropped by the body");
        }
        if (diskDropped != 0)
        {
            failures.Add($"{diskDropped} dropped by the disk");
        }
        if (ser.LongSteps.Length > 0)
        {
            failures.Add($"{ser.LongSteps.Length} step(s) over {ser.LongStepBound.TotalMilliseconds:F0} ms, the longest {ser.MaxStep.TotalMilliseconds:F0} ms");
        }
        return failures;
    }

    /// <summary>A SER step this long, or a SER ending this much before its recording does, looks like a stall.</summary>
    public static readonly TimeSpan StallStep = TimeSpan.FromSeconds(1);

    /// <summary>Where a recording stands: set aside and reported apart, and whether another run is made in its place.</summary>
    public readonly record struct Standing(bool SetAside, bool MakesAnother);

    /// <summary>
    /// Where a recording stands. Only a stall its stream LOGGED sets it aside (the node's stream-end line, the in-process
    /// driver's <c>VideoStalls</c>): a gap the stream did not log as one is the node's or the box's (a reader late past the
    /// hand-off, a disk that held the recorder), and the gate judges it. How the SER looks (a step of
    /// <see cref="StallStep"/> or more, or frames ending that much before the recording) only decides whether another run is
    /// made, since a node's stalls are read from its log once it has exited.
    /// </summary>
    /// <param name="loggedStalls">The stalls the stream logged, or null while not yet known.</param>
    public static Standing StandingOf(int? loggedStalls, SerRateReadBack ser, TimeSpan asked)
    {
        var looksStalled = ser.LongSteps.Any(step => step.Step >= StallStep) || ser.Span < asked - StallStep;
        var logged = loggedStalls > 0;
        return new Standing(SetAside: logged, MakesAnother: logged || looksStalled);
    }

    /// <summary>The same session's in-process rate: the mean of its in-process recordings that did not stall, or null when none did not.</summary>
    public static double? InProcessBaseline(IEnumerable<double> cleanRates)
    {
        double[] rates = [.. cleanRates];
        return rates.Length > 0 ? rates.Average() : null;
    }

    /// <summary>
    /// Whether <paramref name="fps"/> is within <see cref="WithinInProcess"/> of <paramref name="baseline"/>. No baseline, or one
    /// that is not a number, is never within: the comparison is what the gate is for, and a comparison through
    /// <see cref="double.CompareTo(double)"/> (as Shouldly's is) ranks NaN below every rate, so a missing baseline used to pass it.
    /// </summary>
    public static bool IsWithinInProcess(double fps, double? baseline)
        => baseline is { } rate && double.IsFinite(rate) && fps >= (1 - WithinInProcess) * rate;
}

/// <summary>
/// The whole machine's CPU over an interval, the load a rate run was measured under: the share of every core's time that
/// was busy (<c>GetSystemTimes</c>), and the processes that used most of it. A run that misses under load is re-run with
/// its bar unchanged, so the load is reported beside every run.
/// </summary>
internal sealed partial class HostCpuLoad
{
    private readonly long _idle;
    private readonly long _kernel;
    private readonly long _user;
    private readonly Dictionary<int, (string Name, TimeSpan Cpu)> _processes;
    private readonly long _started;

    private HostCpuLoad()
    {
        (_idle, _kernel, _user) = SystemTimes();
        _processes = ProcessTimes();
        _started = Stopwatch.GetTimestamp();
    }

    public static HostCpuLoad Start() => new HostCpuLoad();

    /// <summary>Busy share of all cores since <see cref="Start"/> in percent, the cores, and the five processes that used the most.</summary>
    public string Describe()
    {
        var (idle, kernel, user) = SystemTimes();
        var total = (kernel - _kernel) + (user - _user); // kernel time includes the idle time
        var busy = total > 0 ? 100.0 * (total - (idle - _idle)) / total : double.NaN;
        var seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        var top = ProcessTimes()
            .Select(p => (p.Key, p.Value.Name, Used: _processes.TryGetValue(p.Key, out var before) && before.Name == p.Value.Name ? p.Value.Cpu - before.Cpu : p.Value.Cpu))
            .OrderByDescending(static p => p.Used)
            .Take(5)
            .Select(p => $"{p.Name} {p.Key} {p.Used.TotalSeconds / seconds:F1} cores");
        return $"host CPU {busy:F0} % busy of {Environment.ProcessorCount} over {seconds:F0} s (most: {string.Join(", ", top)})";
    }

    private static (long Idle, long Kernel, long User) SystemTimes()
        => OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : (0, 0, 0);

    private static Dictionary<int, (string Name, TimeSpan Cpu)> ProcessTimes()
    {
        var times = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    times[process.Id] = (process.ProcessName, process.TotalProcessorTime);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Exited, or another account's: not this measurement's to read.
                }
            }
        }
        return times;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}

/// <summary>
/// Waits before a rate run until no build or test of another worktree runs (the controller, 2026-10-16: a measurement at
/// 197 fps never shares the box with another worker's build or test, so a short count cannot be that load): every dotnet,
/// testhost, MSBuild or VBCSCompiler process, and every test executable, whose command line or executable names the text
/// <c>TIANWEN_RATE_PROBE_QUIET_FOR</c> holds (a worktree's folder name). Unset, it waits for nothing.
/// </summary>
/// <remarks>
/// A build started from inside the other worktree with relative paths names it in neither, so the host load reported
/// beside each run (<see cref="HostCpuLoad"/>) stays the arbiter.
/// </remarks>
internal static class OtherWorktreeBuilds
{
    public const string QuietForVar = "TIANWEN_RATE_PROBE_QUIET_FOR";

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    /// <summary>What was waited for, and how long; returns at once when the variable is unset or nothing of it runs.</summary>
    public static async Task<string> WaitUntilQuietAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(QuietForVar) is not { Length: > 0 } text || !OperatingSystem.IsWindows())
        {
            return $"{QuietForVar} unset, no other worktree's builds looked for";
        }

        var clock = Stopwatch.StartNew();
        string? first = null;
        while (true)
        {
            var running = await ListAsync(text, cancellationToken);
            if (running.Length == 0)
            {
                return first is null
                    ? $"nothing of {text} building or testing"
                    : $"waited {clock.Elapsed.TotalSeconds:F0} s for {text}'s build or test to end ({first})";
            }
            first ??= string.Join("; ", running.Take(3));
            await SystemTimeProvider.Instance.SleepAsync(Poll, cancellationToken);
        }
    }

    // Each matching process as "pid name command line", through Win32_Process (the only place another process's command
    // line is), in a pwsh with no window.
    private static async Task<string[]> ListAsync(string text, CancellationToken cancellationToken)
    {
        const string Script = "$t = $env:TIANWEN_RATE_PROBE_QUIET_FOR; Get-CimInstance Win32_Process | "
            + "Where-Object { $_.Name -match '^(dotnet|testhost.*|MSBuild.*|VBCSCompiler.*|.*\\.Tests(\\..*)?)\\.exe$' } | "
            + "Where-Object { ($_.CommandLine -and $_.CommandLine.Contains($t, [StringComparison]::OrdinalIgnoreCase)) -or "
            + "($_.ExecutablePath -and $_.ExecutablePath.Contains($t, [StringComparison]::OrdinalIgnoreCase)) } | "
            + "ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.Name, $_.CommandLine }";
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (ReadOnlySpan<string>)["-NoProfile", "-NonInteractive", "-Command", Script])
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[QuietForVar] = text;
        using var pwsh = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = pwsh.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = pwsh.StandardError.ReadToEndAsync(cancellationToken);
        await pwsh.WaitForExitAsync(cancellationToken);
        var listed = await stdout;
        if (pwsh.ExitCode != 0)
        {
            throw new InvalidOperationException($"listing the processes failed ({pwsh.ExitCode}): {await stderr}");
        }
        return listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith($"{Environment.ProcessId} ", StringComparison.Ordinal))
            .ToArray();
    }
}

/// <summary>
/// When an evidence run prints its clicks for a person: at once, then again each time the interval has passed, until the person
/// has made them.
/// </summary>
internal static class ClickPrompt
{
    /// <summary>
    /// Whether the clicks are due to be printed at <paramref name="now"/>, on a clock started with the run, when they were last
    /// printed at <paramref name="lastPrinted"/>, null before the first print. Never a <see cref="TimeSpan.MinValue"/> for "never":
    /// subtracting it from the clock overflows.
    /// </summary>
    public static bool Due(TimeSpan? lastPrinted, TimeSpan now, TimeSpan every) => lastPrinted is not { } last || now - last >= every;
}

/// <summary>
/// A GUI window an evidence run opens: put on the LEFT half of the primary monitor's work area, full height, never over the
/// right half (where another program may need to be seen), as <c>tools/check-local-only-binds.ps1</c> places it; and
/// captured by <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c>, which reads that window alone, swapchain included, with no
/// activation and no screen copy (a screen copy photographs whatever is on top).
/// </summary>
internal static partial class GuiWindow
{
    /// <summary>
    /// The process's main window once it has one, placed on the left half; null once the process has exited without one. Bounded
    /// by <paramref name="cancellationToken"/> alone, the test's, never a clock (CLAUDE.md, "No wall-clock timeout inside a test").
    /// </summary>
    public static async Task<(nint Window, string Placed)?> PlaceOnLeftHalfAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            process.Refresh();
            if (process.MainWindowHandle != nint.Zero)
            {
                return (process.MainWindowHandle, PlaceOnLeftHalf(process.MainWindowHandle));
            }
            await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        return null;
    }

    private static string PlaceOnLeftHalf(nint window)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var area = default(Rect);
            SystemParametersInfo(SpiGetWorkArea, 0, ref area, 0);
            if (IsZoomed(window))
            {
                ShowWindow(window, SwShowNoActivate);
            }
            var half = (area.Right - area.Left) / 2;
            SetWindowPos(window, nint.Zero, area.Left, area.Top, half, area.Bottom - area.Top, SwpNoZOrder | SwpNoActivate);
            GetWindowRect(window, out var placed);
            var onTheLeft = placed.Right <= area.Left + half + 16; // the invisible resize border may reach a few pixels past
            return $"{(onTheLeft ? "on" : "NOT on")} the left half: ({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom}) "
                + $"of the work area ({area.Left},{area.Top})-({area.Right},{area.Bottom})";
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>
    /// Writes <paramref name="window"/>, which must belong to <paramref name="pid"/>, to <paramref name="path"/> as a PNG;
    /// returns its size in physical pixels.
    /// </summary>
    public static (int Width, int Height) PrintToPng(nint window, int pid, string path)
    {
        GetWindowThreadProcessId(window, out var owner);
        if (owner != pid)
        {
            throw new InvalidOperationException($"the window belongs to process {owner}, not {pid}: nothing is captured");
        }

        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var screen = GetDC(nint.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = nint.Zero;
        var selected = nint.Zero;
        try
        {
            GetWindowRect(window, out var rect);
            var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down rows
                Planes = 1,
                BitCount = 32,
            };
            bitmap = CreateDIBSection(dc, ref header, 0, out var bits, nint.Zero, 0);
            if (bitmap == nint.Zero)
            {
                throw new InvalidOperationException($"no {width}x{height} bitmap to print the window into");
            }
            selected = SelectObject(dc, bitmap);
            if (!PrintWindow(window, dc, PwRenderFullContent))
            {
                throw new InvalidOperationException("PrintWindow refused the window");
            }

            var bgra = new byte[width * height * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            for (var i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                bgra[i + 3] = 255;
            }
            System.IO.File.WriteAllBytes(path, SharpAstro.Png.PngWriter.Encode(bgra, width, height));
            return (width, height);
        }
        finally
        {
            if (selected != nint.Zero)
            {
                SelectObject(dc, selected);
            }
            if (bitmap != nint.Zero)
            {
                DeleteObject(bitmap);
            }
            DeleteDC(dc);
            ReleaseDC(nint.Zero, screen);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static readonly nint PerMonitorAwareV2 = -4;
    private const uint SpiGetWorkArea = 0x30;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint PwRenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref Rect value, uint winIni);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int show);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint dc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}

/// <summary>
/// Every TCP listener and UDP endpoint the given processes own, as Windows' own tables hold them (the IP Helper's owner
/// tables, which <c>Get-NetTCPConnection</c> and <c>Get-NetUDPEndpoint</c> read, and so
/// <c>tools/check-local-only-binds.ps1</c>): sampled in this process, so a run's socket check starts no program beside it.
/// </summary>
internal static partial class OwnedEndpoints
{
    public sealed record Endpoint(int Pid, string Protocol, IPAddress Address, int Port)
    {
        public bool IsLoopback => IPAddress.IsLoopback(Address);

        public override string ToString() => $"pid {Pid} {Protocol,-10} {Address}:{Port}{(IsLoopback ? "" : "   NOT LOOPBACK")}";
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>Adds what <paramref name="pids"/> own now to <paramref name="seen"/>.</summary>
    public static void Sample(IReadOnlyCollection<int> pids, HashSet<Endpoint> seen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Row layouts: MIB_TCPROW_OWNER_PID (24 bytes), MIB_TCP6ROW_OWNER_PID (56), MIB_UDPROW_OWNER_PID (12), MIB_UDP6ROW_OWNER_PID (28).
        Read(tcp: true, AfInet, 24, pids, seen, static row => (V4(row, 4), Port(row, 8), Marshal.ReadInt32(row, 20)), "TCP listen");
        Read(tcp: true, AfInet6, 56, pids, seen, static row => (V6(row, 0), Port(row, 20), Marshal.ReadInt32(row, 52)), "TCP listen");
        Read(tcp: false, AfInet, 12, pids, seen, static row => (V4(row, 0), Port(row, 4), Marshal.ReadInt32(row, 8)), "UDP");
        Read(tcp: false, AfInet6, 28, pids, seen, static row => (V6(row, 0), Port(row, 20), Marshal.ReadInt32(row, 24)), "UDP");
    }

    private static void Read(bool tcp, int family, int rowSize, IReadOnlyCollection<int> pids, HashSet<Endpoint> seen,
        Func<nint, (IPAddress Address, int Port, int Pid)> row, string protocol)
    {
        var size = 0;
        var table = nint.Zero;
        try
        {
            while (true)
            {
                var result = tcp
                    ? GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0)
                    : GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0);
                if (result == 0)
                {
                    break;
                }
                if (result != ErrorInsufficientBuffer)
                {
                    throw new InvalidOperationException($"the {protocol} table for address family {family} could not be read: {result}");
                }
                Marshal.FreeHGlobal(table);
                table = Marshal.AllocHGlobal(size);
            }

            var entries = Marshal.ReadInt32(table);
            for (var i = 0; i < entries; i++)
            {
                var (address, port, pid) = row(table + 4 + i * rowSize);
                if (pids.Contains(pid))
                {
                    seen.Add(new Endpoint(pid, protocol, address, port));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static IPAddress V4(nint row, int offset) => new IPAddress((uint)Marshal.ReadInt32(row, offset));

    private static IPAddress V6(nint row, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + offset, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    // The port is the low 16 bits, in network order.
    private static int Port(nint row, int offset)
    {
        var raw = Marshal.ReadInt32(row, offset);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);
}
