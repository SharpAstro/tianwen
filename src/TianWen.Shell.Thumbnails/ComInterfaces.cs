using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace TianWen.Shell.Thumbnails
{
    // The shell-side contracts, declared for the .NET COM source generator. Vtable order follows
    // unknwn.h / objidl.h / propsys.h / thumbcache.h exactly, and every method is PreserveSig over raw
    // pointers, so the generated stubs are plain thunks: no marshaller can throw across the COM
    // boundary, and an HRESULT is returned rather than translated into an exception either side.

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal unsafe partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint pUnkOuter, Guid* riid, nint* ppvObject);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [GeneratedComInterface]
    [Guid("0c733a30-2a1c-11ce-ade5-00aa0044773d")]
    internal unsafe partial interface ISequentialStream
    {
        [PreserveSig]
        int Read(byte* pv, uint cb, uint* pcbRead);

        [PreserveSig]
        int Write(byte* pv, uint cb, uint* pcbWritten);
    }

    /// <summary>
    /// Only <see cref="ISequentialStream.Read"/> is called; the rest of the vtable is declared so the
    /// slot numbers line up with the interface the shell actually hands over.
    /// </summary>
    [GeneratedComInterface]
    [Guid("0000000c-0000-0000-C000-000000000046")]
    internal unsafe partial interface IStream : ISequentialStream
    {
        [PreserveSig]
        int Seek(long dlibMove, uint dwOrigin, ulong* plibNewPosition);

        [PreserveSig]
        int SetSize(ulong libNewSize);

        [PreserveSig]
        int CopyTo(nint pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten);

        [PreserveSig]
        int Commit(uint grfCommitFlags);

        [PreserveSig]
        int Revert();

        [PreserveSig]
        int LockRegion(ulong libOffset, ulong cb, uint dwLockType);

        [PreserveSig]
        int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType);

        [PreserveSig]
        int Stat(void* pstatstg, uint grfStatFlag);

        [PreserveSig]
        int Clone(nint* ppstm);
    }

    /// <summary>
    /// The initialiser a handler MUST implement to run in the shell's surrogate process, which is the
    /// only place a packaged (MSIX) shell extension is allowed to run. <c>IInitializeWithFile</c> and
    /// <c>IInitializeWithItem</c> need the handler in-process and are deliberately not implemented.
    /// </summary>
    [GeneratedComInterface]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    internal partial interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(IStream pstream, uint grfMode);
    }

    /// <summary>
    /// Optional, and the only lever the shell gives a handler over HOW MUCH work it may do. The one
    /// flag that matters here is <c>WTSCF_FAST</c> (0x8), set when the caller asked for a fast
    /// extraction: "if it is not already cached, answer only from something EMBEDDED in the file".
    /// <para>
    /// Implementing it is what stops this handler pulling a cloud file down. Reading an
    /// <c>IStream</c> over a dehydrated placeholder is what triggers hydration, and a FITS carries no
    /// embedded thumbnail to answer from, so under that flag the only correct answer is to decline.
    /// Without this interface the shell has no way to ask, and a folder of cloud-backed frames would
    /// be downloaded in full just to draw icons.
    /// </para>
    /// </summary>
    [GeneratedComInterface]
    [Guid("f4376f00-bef5-4d45-80f3-1e023bbf1209")]
    internal partial interface IThumbnailSettings
    {
        [PreserveSig]
        int SetContext(uint dwContext);
    }

    [GeneratedComInterface]
    [Guid("e357fccd-a995-4576-b01f-234630154e96")]
    internal unsafe partial interface IThumbnailProvider
    {
        /// <summary>
        /// <paramref name="cx"/> is the requested square edge; the handler returns a bitmap no larger
        /// than that on either side, aspect preserved (the shell pads). The HBITMAP is owned by the
        /// caller. <paramref name="pdwAlpha"/> is a <c>WTS_ALPHATYPE</c>: 1 = RGB, 2 = ARGB.
        /// </summary>
        [PreserveSig]
        int GetThumbnail(uint cx, nint* phbmp, int* pdwAlpha);
    }

    /// <summary>
    /// <c>PROPERTYKEY</c> (wtypes.h): a format id and a property id, 20 bytes on every architecture.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    /// <summary>
    /// <c>PROPVARIANT</c> (propidl.h), declared IN FULL: 24 bytes on x64 and arm64 (a 2-byte type, three
    /// reserved words, then a 16-byte union whose widest member is a pointer pair such as <c>CALPWSTR</c> or
    /// <c>BLOB</c>). The caller of <c>GetValue</c> owns the struct and the callee writes all of it, so a
    /// trimmed declaration would have the shell clear 24 bytes over a shorter one, or the handler write
    /// past it. The union is two 8-byte slots: <see cref="data"/> at offset 8 (a number, a FILETIME, or a
    /// vector's element count in its low 4 bytes) and <see cref="data2"/> at offset 16 (the vector's element
    /// pointer, as a <c>CALPWSTR</c> is <c>{ULONG cElems; PWSTR* pElems}</c>). Strings and vectors are
    /// <c>CoTaskMemAlloc</c>'d for the caller's <c>PropVariantClear</c> to free.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public ulong data;
        public ulong data2;
    }

    /// <summary>propsys.h; a read-only store implements the four reads and refuses the two writes.</summary>
    [GeneratedComInterface]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal unsafe partial interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(uint* cProps);

        [PreserveSig]
        int GetAt(uint iProp, PROPERTYKEY* pkey);

        [PreserveSig]
        int GetValue(PROPERTYKEY* key, PROPVARIANT* pv);

        [PreserveSig]
        int SetValue(PROPERTYKEY* key, PROPVARIANT* propvar);

        [PreserveSig]
        int Commit();
    }

    /// <summary>
    /// propsys.h. <c>IsPropertyWritable</c> answers <c>S_OK</c> for writable and <c>S_FALSE</c> for not.
    /// </summary>
    [GeneratedComInterface]
    [Guid("c8e2d566-186e-4d49-bf41-6909ead56acc")]
    internal unsafe partial interface IPropertyStoreCapabilities
    {
        [PreserveSig]
        int IsPropertyWritable(PROPERTYKEY* key);
    }
}
