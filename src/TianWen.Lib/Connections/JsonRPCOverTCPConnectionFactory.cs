using System.Collections.Generic;
using System.Net;

namespace TianWen.Lib.Connections;

internal class JsonRPCOverTCPConnectionFactory : IUtf8TextBasedConnectionFactory
{
    public IReadOnlyList<CommunicationProtocol> SupportedHighLevelProtocols { get; } = [CommunicationProtocol.JsonRPC];

    public IUtf8TextBasedConnection Connect(EndPoint endPoint, CommunicationProtocol highLevelProtocol)
    {
        var connection = new JsonRPCOverTcpConnection();
        connection.Connect(endPoint);
        return connection;
    }
}
