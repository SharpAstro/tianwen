using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface IUtf8TextBasedConnectionFactory
{
    IReadOnlyList<CommunicationProtocol> SupportedHighLevelProtocols { get; }

    Task<IUtf8TextBasedConnection> ConnectAsync(EndPoint endPoint, CommunicationProtocol highLevelProtocol, CancellationToken cancellationToken = default);
}
