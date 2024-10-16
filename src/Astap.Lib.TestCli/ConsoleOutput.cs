using Astap.Lib.Devices;
using Microsoft.Extensions.Logging;
using Pastel;

class ConsoleOutput(DirectoryInfo outputFolder) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = outputFolder;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public void Log(LogLevel logLevel, string message)
    {
        Action<string> writeLineFun;
        if (logLevel >= LogLevel.Error)
        {
            writeLineFun = Console.Error.WriteLine;
        }
        else
        {
            writeLineFun = Console.WriteLine;
        }

        var color = logLevel switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Information => ConsoleColor.White,
            _ => ConsoleColor.Gray
        };

        writeLineFun($"[{TimeProvider.GetUtcNow():o}] {message.Pastel(color)}");
    }
}