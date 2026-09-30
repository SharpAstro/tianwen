using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.IO;

/// <summary>A file inside a 7z archive, as 7-Zip lists it: its path in the archive and its unpacked size.</summary>
public readonly record struct ArchiveMember(string Path, long Size);

/// <summary>
/// The 7-Zip command line, for the planetary corpus's archives (docs/plans/planetary-restoration.md, R0): list an
/// archive's members, read a member's first bytes (a SER header) without unpacking it, and unpack one member at a time,
/// so a 145 GB archive never needs 145 GB free. Every call is bounded (<see cref="BoundedProcess"/>): the tool never
/// outlives the call.
/// </summary>
public sealed class SevenZipTool(string executable)
{
    /// <summary>The <c>7z</c> this runs.</summary>
    public string Executable { get; } = executable;

    /// <summary>
    /// The 7-Zip to use: <paramref name="explicitPath"/> when given, else <c>7z</c> on the PATH, else the default
    /// Windows install. Null when there is none.
    /// </summary>
    public static SevenZipTool? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath) ? new SevenZipTool(explicitPath) : null;
        }
        var names = OperatingSystem.IsWindows() ? new[] { "7z.exe" } : new[] { "7z", "7zz", "7za" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return new SevenZipTool(candidate);
                }
            }
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var candidate = Path.Combine(Environment.GetFolderPath(folder), "7-Zip", "7z.exe");
                if (File.Exists(candidate))
                {
                    return new SevenZipTool(candidate);
                }
            }
        }
        return null;
    }

    /// <summary>The archive's files (never its directories), in the order 7-Zip lists them.</summary>
    public async Task<ImmutableArray<ArchiveMember>> ListAsync(string archive, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Start(["l", "-slt", "-ba", archive], redirectOutput: true);
        var listing = process.StandardOutput.ReadToEndAsync(cancellationToken);
        if (!await BoundedProcess.WaitForExitOrKillAsync(process, timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException($"7-Zip did not list {archive} within {timeout.TotalSeconds:0} s");
        }
        var text = await listing.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException($"7-Zip could not list {archive} (exit {process.ExitCode})");
        }
        return ParseListing(text);
    }

    /// <summary>
    /// Reads the first <paramref name="count"/> bytes of <paramref name="member"/> by streaming it and stopping 7-Zip
    /// once they are in. Fewer come back only when the member is shorter.
    /// </summary>
    public async Task<byte[]> ReadPrefixAsync(string archive, string member, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        using var process = Start(["e", "-so", archive, member], redirectOutput: true);
        using var timeoutCts = new CancellationTokenSource(timeout, TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var buffer = new byte[count];
        var read = 0;
        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), linked.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"7-Zip did not stream {member} from {archive} within {timeout.TotalSeconds:0} s");
        }
        finally
        {
            // The rest of the member is not wanted: stop the stream rather than drain it.
            BoundedProcess.KillTree(process);
        }
        return read == count ? buffer : buffer[..read];
    }

    /// <summary>Unpacks <paramref name="member"/> alone into <paramref name="directory"/> and answers where it landed.</summary>
    public async Task<string> ExtractAsync(string archive, string member, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        using var process = Start(["e", "-y", "-o" + directory, archive, member], redirectOutput: true);
        var drain = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await BoundedProcess.WaitForExitOrKillAsync(process, timeout: null, cancellationToken).ConfigureAwait(false);
        await drain.ConfigureAwait(false);
        var landed = Path.Combine(directory, Path.GetFileName(member.Replace('\\', '/')));
        if (process.ExitCode != 0 || !File.Exists(landed))
        {
            throw new IOException($"7-Zip could not unpack {member} from {archive} (exit {process.ExitCode})");
        }
        return landed;
    }

    /// <summary>The files of a <c>7z l -slt -ba</c> listing: its blocks of <c>Key = Value</c> lines, directories dropped.</summary>
    public static ImmutableArray<ArchiveMember> ParseListing(string listing)
    {
        var members = ImmutableArray.CreateBuilder<ArchiveMember>();
        string? path = null;
        long size = -1;
        var directory = false;

        void Flush()
        {
            if (path is not null && !directory && size >= 0)
            {
                members.Add(new ArchiveMember(path, size));
            }
            path = null;
            size = -1;
            directory = false;
        }

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            var key = separator < 0 ? line.TrimEnd(' ', '=') : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 3)..];
            switch (key)
            {
                case "Path":
                    Flush();
                    path = value;
                    break;
                case "Size":
                    size = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : -1;
                    break;
                case "Attributes":
                    directory = value.StartsWith('D');
                    break;
                case "Folder":
                    directory |= value == "+";
                    break;
            }
        }
        Flush();
        return members.ToImmutable();
    }

    private Process Start(IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        var process = Process.Start(start) ?? throw new IOException($"Could not start {Executable}");
        // Standard error is drained so a chatty 7-Zip can never block on a full pipe.
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }
}
