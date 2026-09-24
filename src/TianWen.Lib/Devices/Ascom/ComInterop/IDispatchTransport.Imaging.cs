namespace TianWen.Lib.Devices.Ascom.ComInterop;

// The frame-channel read needs TianWen.Lib's imaging types, and tianwen-ascomhost compiles
// IDispatchTransport.cs as a linked source with the BCL and COM interop only (TianWen.AscomHost.csproj),
// so it is this part, which only TianWen.Lib compiles. The host never needs it: its side of the JSON-RPC
// seam serves the ImageArray as an int[,], which the default below turns into the channel here.
internal partial interface IDispatchTransport
{
    /// <summary>
    /// A camera's 2-D image property (<c>ImageArray</c>) as a frame channel, into <paramref name="recycled"/>
    /// when its shape matches. The default is the portable path, <see cref="GetInt2DArray"/> then the
    /// transpose, which the JSON-RPC transport keeps; the in-proc <see cref="DispatchObject"/> reads the
    /// SAFEARRAY straight into the plane instead (<see cref="SafeArrayMarshal.ToImageChannel"/>).
    /// </summary>
    Imaging.Channel GetImageChannel(string name, float[,]? recycled)
        => Imaging.Channel.FromWxHImageData(GetInt2DArray(name), recycled);
}
