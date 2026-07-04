using System;

namespace TianWen.Lib.Connections;

/// <summary>Thrown when a JSON-RPC call fails to send, times out its correlation, or the peer returns an <c>error</c> object.</summary>
internal sealed class JsonRpcException(string message, int? code = null) : Exception(message)
{
    /// <summary>The JSON-RPC <c>error.code</c>, when the peer supplied one.</summary>
    public int? Code { get; } = code;
}
