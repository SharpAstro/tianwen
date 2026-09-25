using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// A process started so it outlives the one that started it: the node's keeper, which a client starts
    /// (docs/plans/hardware-in-the-server.md, "Spawn and lifetime"). On Windows it breaks away from the client's job and
    /// has no window (<see cref="StartOnWindows"/>); on Unix it is started plainly, and the keeper leaves the client's
    /// session and terminal itself (<c>NodeDetachment</c>), since a child of <c>Process.Start</c> can.
    /// </summary>
    internal sealed partial class DetachedProcess : IDisposable
    {
        private readonly Process? _process;

        private DetachedProcess(int id, bool outlivesClient, Process? process)
        {
            Id = id;
            OutlivesClient = outlivesClient;
            _process = process;
        }

        public int Id { get; }

        /// <summary>
        /// False when the process could not be broken away from the client's job (a launcher or a debugger whose job
        /// forbids it), so it ends when the job does.
        /// </summary>
        public bool OutlivesClient { get; }

        /// <summary>
        /// Starts <paramref name="path"/> in <paramref name="workingDirectory"/>, with this process's environment as
        /// <paramref name="environment"/> changes it (a null value removes a variable).
        /// </summary>
        public static DetachedProcess Start(string path, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
        {
            if (OperatingSystem.IsWindows())
            {
                return StartOnWindows(path, arguments, workingDirectory, environment);
            }

            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            foreach (var (name, value) in environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(name);
                }
                else
                {
                    start.Environment[name] = value;
                }
            }

            var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}");
            return new DetachedProcess(process.Id, outlivesClient: true, process);
        }

        /// <summary>The process's exit code, once it has exited.</summary>
        public bool TryGetExitCode([NotNullWhen(true)] out int? exitCode)
        {
            if (_process is { } process)
            {
                exitCode = process.HasExited ? process.ExitCode : null;
                return exitCode is not null;
            }

            return TryGetExitCodeOnWindows(out exitCode);
        }

        public void Dispose()
        {
            _process?.Dispose();
            DisposeOnWindows();
        }
    }
}
