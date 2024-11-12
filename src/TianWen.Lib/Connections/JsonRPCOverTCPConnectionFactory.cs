using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal class JsonRPCOverTCPConnectionFactory : IUtf8TextBasedConnectionFactory
{
    public IReadOnlyList<CommunicationProtocol> SupportedHighLevelProtocols { get; } = [CommunicationProtocol.JsonRPC];

    public async Task<IUtf8TextBasedConnection> ConnectAsync(EndPoint endPoint, CommunicationProtocol highLevelProtocol, CancellationToken cancellationToken = default)
    {
        if (highLevelProtocol is not CommunicationProtocol.JsonRPC)
        {
            throw new InvalidOperationException($"Protocol {highLevelProtocol} is not supported");
        }

        var connection = new JsonRPCOverTcpConnection();
        await connection.ConnectAsync(endPoint, cancellationToken);
        return connection;
    }
}
