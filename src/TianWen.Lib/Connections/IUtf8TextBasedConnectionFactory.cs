using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface IUtf8TextBasedConnectionFactory
{
    IReadOnlyList<CommunicationProtocol> SupportedHighLevelProtocols { get; }

    IUtf8TextBasedConnection Connect(EndPoint endPoint, CommunicationProtocol highLevelProtocol);
    Task<IUtf8TextBasedConnection> ConnectAsync(EndPoint endPoint, CommunicationProtocol highLevelProtocol);
}
