using TianWen.Lib.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pastel;
using System.Reflection;

record ConsoleOptions(string OutputFolder);

class ConsoleExternal(IOptions<ConsoleOptions> options) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = new DirectoryInfo(options.Value.OutputFolder);

    public DirectoryInfo ProfileFolder =>
        new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create)).CreateSubdirectory(
            (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Name ?? "TianWen.Lib"
        );
            
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