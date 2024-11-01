using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace TianWen.Lib.Devices;

internal class SystemExternal(IUtf8TextBasedConnectionFactory textBasedConnectionFactory, ILoggerFactory loggerFactory) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = CreateSpecialSubFolder(Environment.SpecialFolder.MyPictures);

    public DirectoryInfo ProfileFolder { get; } = CreateSpecialSubFolder(Environment.SpecialFolder.ApplicationData);

    private static DirectoryInfo CreateSpecialSubFolder(Environment.SpecialFolder specialFolder) =>
        new DirectoryInfo(Environment.GetFolderPath(specialFolder, Environment.SpecialFolderOption.Create)).CreateSubdirectory(IExternal.ApplicationName);

    public ILogger AppLogger => loggerFactory.CreateLogger<SystemExternal>();

    public IReadOnlyList<string> EnumerateSerialPorts() => StreamBasedSerialPort.EnumerateSerialPorts();

    public ISerialDevice OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => new StreamBasedSerialPort(address, baud, AppLogger, encoding, ioTimeout);

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public IUtf8TextBasedConnection ConnectGuider(EndPoint address) => textBasedConnectionFactory.Connect(address);
}