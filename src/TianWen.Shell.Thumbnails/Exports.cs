using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TianWen.Lib.Imaging;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// The two entry points COM requires of an in-process server, exported by the NativeAOT compiler
    /// from the <see cref="UnmanagedCallersOnlyAttribute.EntryPoint"/> names. There is deliberately no
    /// <c>DllRegisterServer</c>: registration is the MSIX manifest's job for the Store install and
    /// <c>FileAssociationRegistrar</c>'s for the tarball, and a self-registering DLL would be a third
    /// copy of the same keys.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe class Exports
    {
        private const int S_FALSE = 1;
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

        private static readonly Guid IidIClassFactory = new Guid("00000001-0000-0000-C000-000000000046");

        [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
        public static int DllGetClassObject(Guid* rclsid, Guid* riid, nint* ppv)
        {
            *ppv = 0;
            if (*rclsid != ThumbnailRenderer.ShellExtensionClsid)
            {
                return CLASS_E_CLASSNOTAVAILABLE;
            }

            if (*riid != IidIClassFactory)
            {
                return E_NOINTERFACE;
            }

            var pUnk = (nint)ComInterfaceMarshaller<ThumbnailClassFactory>.ConvertToUnmanaged(ThumbnailClassFactory.Instance);
            var hr = Marshal.QueryInterface(pUnk, in *riid, out *ppv);
            Marshal.Release(pUnk);
            return hr;
        }

        /// <summary>
        /// Always "no": a NativeAOT library cannot be unloaded (FreeLibrary is unsupported by the runtime).
        /// Harmless here, because the shell's surrogate process exits on its own idle timer and takes the
        /// DLL with it, which is how every thumbnail handler leaves memory anyway.
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
        public static int DllCanUnloadNow() => S_FALSE;
    }
}
