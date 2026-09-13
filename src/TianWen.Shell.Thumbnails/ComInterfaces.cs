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
}
