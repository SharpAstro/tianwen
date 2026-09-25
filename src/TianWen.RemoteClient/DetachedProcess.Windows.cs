using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TianWen.RemoteClient
{
    internal sealed partial class DetachedProcess
    {
        // The Windows half: CreateProcessW, since Process.Start exposes no creation flags.
        private SafeProcessHandle? _handle;

        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateNewProcessGroup = 0x00000200;
        private const int ErrorAccessDenied = 5;
        private const uint StillActive = 259;

        /// <summary>
        /// Breaks away from the client's job, so a launcher or a terminal that kills its job on exit does not take the
        /// node with it; has no window; and starts a process group of its own, so a Ctrl+C in the client's console does
        /// not reach it. Inherits no handle. A job that forbids breaking away refuses the first attempt, and the process
        /// is then started plainly, to end with that job (<see cref="OutlivesClient"/> false), rather than not at all.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static DetachedProcess StartOnWindows(string path, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
        {
            var commandLine = CommandLine(path, arguments);
            var block = EnvironmentBlock(environment);
            const uint Flags = CreateNoWindow | CreateUnicodeEnvironment | CreateNewProcessGroup;

            if (TryCreate(commandLine, block, workingDirectory, Flags | CreateBreakawayFromJob, out var info, out var error))
            {
                return Wrap(info, outlivesClient: true);
            }
            if (error == ErrorAccessDenied && TryCreate(commandLine, block, workingDirectory, Flags, out info, out error))
            {
                return Wrap(info, outlivesClient: false);
            }

            throw new Win32Exception(error, $"Could not start {path}");
        }

        [SupportedOSPlatform("windows")]
        private static DetachedProcess Wrap(ProcessInformation info, bool outlivesClient)
        {
            CloseHandle(info.Thread);
            return new DetachedProcess(info.ProcessId, outlivesClient, process: null) { _handle = new SafeProcessHandle(info.Process, ownsHandle: true) };
        }

        [SupportedOSPlatform("windows")]
        private static unsafe bool TryCreate(string commandLine, string environmentBlock, string workingDirectory, uint flags, out ProcessInformation info, out int error)
        {
            var startup = new StartupInfo { Size = sizeof(StartupInfo) };
            // CreateProcessW may write to the command line, so it gets a copy of its own.
            var line = (commandLine + '\0').ToCharArray();
            fixed (char* linePointer = line)
            fixed (char* environmentPointer = environmentBlock)
            {
                if (CreateProcess(null, linePointer, 0, 0, inheritHandles: false, flags, environmentPointer, workingDirectory, ref startup, out info))
                {
                    error = 0;
                    return true;
                }
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }

        private bool TryGetExitCodeOnWindows([NotNullWhen(true)] out int? exitCode)
        {
            if (OperatingSystem.IsWindows() && _handle is { } handle && GetExitCodeProcess(handle, out var code) && code != StillActive)
            {
                exitCode = unchecked((int)code);
                return true;
            }

            exitCode = null;
            return false;
        }

        private void DisposeOnWindows() => _handle?.Dispose();

        /// <summary>
        /// One command line from an executable and its arguments, quoted as the C runtime splits it back: an argument
        /// with a space, a tab or a quote in it is quoted, a quote inside is escaped, and the backslashes before a quote
        /// or the closing quote are doubled.
        /// </summary>
        internal static string CommandLine(string path, IReadOnlyList<string> arguments)
        {
            var line = new StringBuilder();
            Append(line, path);
            foreach (var argument in arguments)
            {
                line.Append(' ');
                Append(line, argument);
            }
            return line.ToString();

            static void Append(StringBuilder line, string argument)
            {
                if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
                {
                    line.Append(argument);
                    return;
                }

                line.Append('"');
                for (var i = 0; ; i++)
                {
                    var backslashes = 0;
                    while (i < argument.Length && argument[i] == '\\')
                    {
                        i++;
                        backslashes++;
                    }

                    if (i == argument.Length)
                    {
                        line.Append('\\', backslashes * 2);
                        break;
                    }

                    if (argument[i] == '"')
                    {
                        line.Append('\\', backslashes * 2 + 1).Append('"');
                    }
                    else
                    {
                        line.Append('\\', backslashes).Append(argument[i]);
                    }
                }
                line.Append('"');
            }
        }

        /// <summary>
        /// This process's environment as <paramref name="changes"/> changes it, as the block CreateProcessW takes:
        /// <c>name=value</c> entries, each ended by a NUL, sorted by name without regard to case as Windows expects,
        /// and the block ended by one more NUL.
        /// </summary>
        internal static string EnvironmentBlock(IReadOnlyDictionary<string, string?> changes)
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && entry.Value is string value)
                {
                    variables[name] = value;
                }
            }
            foreach (var (name, value) in changes)
            {
                if (value is null)
                {
                    variables.Remove(name);
                }
                else
                {
                    variables[name] = value;
                }
            }

            var block = new StringBuilder();
            foreach (var (name, value) in variables.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                block.Append(name).Append('=').Append(value).Append('\0');
            }
            return block.Append('\0').ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            public nint Reserved;
            public nint Desktop;
            public nint Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public nint Reserved2;
            public nint StdInput;
            public nint StdOutput;
            public nint StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public nint Process;
            public nint Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool CreateProcess(
            string? applicationName,
            char* commandLine,
            nint processAttributes,
            nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            char* environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
