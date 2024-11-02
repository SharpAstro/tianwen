using System;
using System.Net;

namespace TianWen.Lib.Connections;

public interface IUtf8TextBasedConnection : IDisposable
{
    CommunicationProtocol HighLevelProtocol { get; }

    void Connect(EndPoint endPoint);

    bool IsConnected { get; }

    string? ReadLine();

    bool WriteLine(ReadOnlyMemory<byte> message);
}
