using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Imaging;

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

    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken) => await Task.Delay(duration, cancellationToken);

    public Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default)
        => textBasedConnectionFactory.ConnectAsync(address, protocol, cancellationToken);

    public async ValueTask WriteFitsFileAsync(Image image, string fileName)
    {
        await Task.Run(() => image.WriteToFitsFile(fileName)).ConfigureAwait(false);
    }
}