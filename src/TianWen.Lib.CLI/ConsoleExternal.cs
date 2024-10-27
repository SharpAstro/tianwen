using Microsoft.Extensions.Logging;
using System.Text;
using TianWen.Lib.Devices;

class ConsoleExternal(ILoggerFactory loggerFactory) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = 
        new DirectoryInfo(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.Create)
        ).CreateSubdirectory(IExternal.ApplicationName);

    public DirectoryInfo ProfileFolder { get; } =
        new DirectoryInfo(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create)
        ).CreateSubdirectory(IExternal.ApplicationName);

    public ILogger AppLogger => loggerFactory.CreateLogger<ConsoleExternal>();

    public ISerialDevice OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => new StreamBasedSerialPort(address, baud, AppLogger, encoding, ioTimeout);

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}