using System.Net;

namespace TianWen.Lib.Devices.Guider;

internal class JsonRPCConnectionFactory : IUtf8TextBasedConnectionFactory
{
    public IUtf8TextBasedConnection Connect(EndPoint endPoint)
    {
        var connection = new JsonRPCConnection();
        connection.Connect(endPoint);
        return connection;
    }
}
