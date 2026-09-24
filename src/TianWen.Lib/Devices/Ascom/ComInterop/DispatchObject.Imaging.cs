namespace TianWen.Lib.Devices.Ascom.ComInterop;

// The frame-channel read needs TianWen.Lib's imaging types, and tianwen-ascomhost compiles
// DispatchObject.cs as a linked source with the BCL and COM interop only (TianWen.AscomHost.csproj),
// so it is this part, which only TianWen.Lib compiles.
internal sealed partial class DispatchObject
{
    public Imaging.Channel GetImageChannel(string name, float[,]? recycled)
    {
        // The SAFEARRAY is read while the VARIANT that owns it is alive, straight into the plane.
        var variant = GetPropertyVariant(name);
        try
        {
            return SafeArrayMarshal.ToImageChannel(SafeArrayPtr(ref variant), recycled);
        }
        finally
        {
            variant.Dispose();
        }
    }
}
