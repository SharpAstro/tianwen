using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
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
) : IExternal, IDisposable
{
    private readonly SemaphoreSlim _serialPortEnumerationSemaphore = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, ISerialConnection> _serialConnections = [];
    private bool disposedValue;

    public TimeProvider TimeProvider => TimeProvider.System;

    public DirectoryInfo OutputFolder { get; } = Environment.SpecialFolder.MyPictures.CreateAppSubFolder();

    public DirectoryInfo ProfileFolder { get; } = Environment.SpecialFolder.ApplicationData.CreateAppSubFolder();

    public ILogger AppLogger => loggerFactory.CreateLogger("App");

    public IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock resourceLock)
    {
        var existingPorts = SerialConnection.EnumerateSerialPorts();

        var availablePorts = new List<string>(existingPorts.Count);

        foreach (var port in existingPorts)
        {
            if (!_serialConnections.TryGetValue(port, out var connection) || !connection.IsOpen)
            {
                availablePorts.Add(port);
            }
        }

        return availablePorts;
    }

    public ValueTask<ResourceLock> WaitForSerialPortEnumerationAsync(CancellationToken cancellationToken) => _serialPortEnumerationSemaphore.AcquireLockAsync(cancellationToken);

    public ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding)
    {
        return _serialConnections.AddOrUpdate(address,
            OpenSerialConnection,
            (portName, existing) => existing.IsOpen ? existing : OpenSerialConnection(portName)
        );

        ISerialConnection OpenSerialConnection(string portName) => new SerialConnection(portName, baud, encoding, AppLogger);
    }

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken) => await Task.Delay(duration, cancellationToken);

    public Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default)
        => textBasedConnectionFactory.ConnectAsync(address, protocol, cancellationToken);

    public async ValueTask WriteFitsFileAsync(Image image, string fileName)
    {
        await Task.Run(() => image.WriteToFitsFile(fileName)).ConfigureAwait(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _serialPortEnumerationSemaphore.Dispose();

                foreach (var serialConnection in _serialConnections.Values)
                {
                    serialConnection.Dispose();
                }
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue=true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~External()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}