using System.Collections.Generic;
using System.Net;

namespace TianWen.Lib.Connections;

public interface IUtf8TextBasedConnectionFactory
{
    IReadOnlyList<CommunicationProtocol> SupportedHighLevelProtocols { get; }

    IUtf8TextBasedConnection Connect(EndPoint endPoint, CommunicationProtocol highLevelProtocol);
}
