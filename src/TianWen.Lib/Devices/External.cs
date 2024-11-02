using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices;

internal class External(
    IUtf8TextBasedConnectionFactory textBasedConnectionFactory,
    ILoggerFactory loggerFactory
) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = CreateSpecialSubFolder(Environment.SpecialFolder.MyPictures);

    public DirectoryInfo ProfileFolder { get; } = CreateSpecialSubFolder(Environment.SpecialFolder.ApplicationData);

    private static DirectoryInfo CreateSpecialSubFolder(Environment.SpecialFolder specialFolder) =>
        new DirectoryInfo(Environment.GetFolderPath(specialFolder, Environment.SpecialFolderOption.Create)).CreateSubdirectory(IExternal.ApplicationName);

    public ILogger AppLogger => loggerFactory.CreateLogger("App");

    public IReadOnlyList<string> EnumerateSerialPorts() => SerialConnection.EnumerateSerialPorts();

    public ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => new SerialConnection(address, baud, AppLogger, encoding, ioTimeout);

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public IUtf8TextBasedConnection ConnectGuider(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC)
        => textBasedConnectionFactory.Connect(address, protocol);
}