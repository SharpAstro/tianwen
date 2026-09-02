using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>Hands the shell a fresh <see cref="AstroThumbnailProvider"/> per request.</summary>
    [GeneratedComClass]
    [SupportedOSPlatform("windows")]
    internal sealed unsafe partial class ThumbnailClassFactory : IClassFactory
    {
        private const int S_OK = 0;
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

        public static ThumbnailClassFactory Instance { get; } = new ThumbnailClassFactory();

        public int CreateInstance(nint pUnkOuter, Guid* riid, nint* ppvObject)
        {
            *ppvObject = 0;
            if (pUnkOuter != 0)
            {
                return CLASS_E_NOAGGREGATION;
            }

            // Wrap once as IUnknown, then let COM's own QueryInterface pick the requested interface, so a
            // request for IInitializeWithStream, IThumbnailProvider or IUnknown all take the same path.
            var provider = new AstroThumbnailProvider();
            var pUnk = (nint)ComInterfaceMarshaller<AstroThumbnailProvider>.ConvertToUnmanaged(provider);
            var hr = Marshal.QueryInterface(pUnk, in *riid, out *ppvObject);
            Marshal.Release(pUnk);
            return hr;
        }

        public int LockServer(bool fLock) => S_OK;
    }
}
