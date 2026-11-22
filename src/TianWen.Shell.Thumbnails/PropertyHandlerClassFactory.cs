using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>Hands the shell a fresh <see cref="AstroPropertyStore"/> per request.</summary>
    [GeneratedComClass]
    [SupportedOSPlatform("windows")]
    internal sealed unsafe partial class PropertyHandlerClassFactory : IClassFactory
    {
        private const int S_OK = 0;
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

        public static PropertyHandlerClassFactory Instance { get; } = new PropertyHandlerClassFactory();

        public int CreateInstance(nint pUnkOuter, Guid* riid, nint* ppvObject)
        {
            *ppvObject = 0;
            if (pUnkOuter != 0)
            {
                return CLASS_E_NOAGGREGATION;
            }

            var store = new AstroPropertyStore();
            var pUnk = (nint)ComInterfaceMarshaller<AstroPropertyStore>.ConvertToUnmanaged(store);
            var hr = Marshal.QueryInterface(pUnk, in *riid, out *ppvObject);
            Marshal.Release(pUnk);
            return hr;
        }

        public int LockServer(bool fLock) => S_OK;
    }
}
