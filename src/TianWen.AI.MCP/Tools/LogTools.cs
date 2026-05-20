using System;
using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;

namespace TianWen.AI.MCP.Tools;

/// <summary>
/// Log file tools -- read-only access to TianWen's daily log files under
/// %LOCALAPPDATA%/TianWen/Logs/ (GUI_*.log, CLI_*.log, FitsViewer_*.log,
/// stack-run-*.log, etc.) plus any other log path the caller hands in.
/// Phase A surfaces a tail; Phase G will add grep.
/// </summary>
[McpServerToolType]
public class LogTools
{
    [McpServerTool, Description("Return the last N lines of a log file. UTF-8 read, allows concurrent writers (FileShare.ReadWrite).")]
    public static string Tail(
        [Description("Absolute path to a log file.")] string path,
        [Description("Number of trailing lines to return (default 100). Cap 5000.")] int lines = 100)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";

        var cap = Math.Clamp(lines, 1, 5000);
        // Ring buffer keeps memory O(cap) regardless of file size.
        var ring = new string[cap];
        var count = 0;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            ring[count % cap] = line;
            count++;
        }

        if (count == 0) return string.Empty;

        var taken = Math.Min(count, cap);
        var start = count >= cap ? count % cap : 0;
        var output = new string[taken];
        for (var i = 0; i < taken; i++)
        {
            output[i] = ring[(start + i) % cap];
        }
        return string.Join('\n', output);
    }
}
