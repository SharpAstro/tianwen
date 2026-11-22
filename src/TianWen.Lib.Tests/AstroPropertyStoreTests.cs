using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Shell.Thumbnails;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Explorer property handler's POLICY and marshalling (#2429): it reads nothing until asked, reads the header
/// and never the pixels, answers each key with the right VARTYPE, refuses every write, and turns junk into an empty
/// store. The values themselves are <see cref="ExplorerFilePropertiesTests"/>'s.
/// </summary>
[SupportedOSPlatform("windows")]
public unsafe class AstroPropertyStoreTests
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private const ushort VT_EMPTY = 0;
    private const ushort VT_R8 = 5;
    private const ushort VT_UI2 = 18;
    private const ushort VT_UI4 = 19;
    private const ushort VT_UI8 = 21;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_FILETIME = 64;
    private const ushort VT_VECTOR = 0x1000;

    private static readonly Guid FmtPhoto = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
    private static readonly Guid FmtSummary = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid FmtImage = new("6444048F-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid FmtMedia = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid FmtVideo = new("64440491-4C8B-11D1-8B70-080036B11A03");

    private static readonly PROPERTYKEY CameraModel = new(FmtPhoto, 272);
    private static readonly PROPERTYKEY CameraManufacturer = new(FmtPhoto, 271);
    private static readonly PROPERTYKEY LensModel = new(new("E1277516-2B5F-4869-89B1-2E585BD38B7A"), 100);
    private static readonly PROPERTYKEY LensManufacturer = new(new("E6DDCAF7-29C5-4F0A-9A68-D19412EC7090"), 100);
    private static readonly PROPERTYKEY ExposureTime = new(FmtPhoto, 33434);
    private static readonly PROPERTYKEY FocalLength = new(FmtPhoto, 37386);
    private static readonly PROPERTYKEY FNumber = new(FmtPhoto, 33437);
    private static readonly PROPERTYKEY IsoSpeed = new(FmtPhoto, 34855);
    private static readonly PROPERTYKEY DateTaken = new(FmtPhoto, 36867);
    private static readonly PROPERTYKEY Subject = new(FmtSummary, 3);
    private static readonly PROPERTYKEY Keywords = new(FmtSummary, 5);
    private static readonly PROPERTYKEY Author = new(FmtSummary, 4);
    private static readonly PROPERTYKEY ApplicationName = new(FmtSummary, 18);
    private static readonly PROPERTYKEY Dimensions = new(FmtImage, 13);
    private static readonly PROPERTYKEY HorizontalSize = new(FmtImage, 3);
    private static readonly PROPERTYKEY VerticalSize = new(FmtImage, 4);
    private static readonly PROPERTYKEY BitDepth = new(FmtImage, 7);
    private static readonly PROPERTYKEY FrameWidth = new(FmtVideo, 3);
    private static readonly PROPERTYKEY FrameHeight = new(FmtVideo, 4);
    private static readonly PROPERTYKEY Duration = new(FmtMedia, 3);
    private static readonly PROPERTYKEY FrameRate = new(FmtVideo, 6);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PROPVARIANT* pvar);

    [Fact]
    public void InitializeTakesTheStreamWithoutReadingAByteOfIt()
    {
        var stream = new SeekableStream(FitsBytes(1000, 1000, 16, ("INSTRUME", "'ZWO ASI533MC Pro'")));
        var store = new AstroPropertyStore();

        store.Initialize(stream, 0).ShouldBe(S_OK);

        stream.Reads.ShouldBe(0);
        stream.Seeks.ShouldBe(0);
    }

    [Fact]
    public void TheFirstPropertyCallReadsTheHeaderOnlyAndTheLaterOnesReadNothing()
    {
        var bytes = FitsBytes(1000, 1000, 16, ("INSTRUME", "'ZWO ASI533MC Pro'"), ("EXPTIME", "300.0"));
        var stream = new SeekableStream(bytes);
        var store = new AstroPropertyStore();
        store.Initialize(stream, 0).ShouldBe(S_OK);

        uint count = 0;
        store.GetCount(&count).ShouldBe(S_OK);

        count.ShouldBeGreaterThan(0u);
        stream.BytesRead.ShouldBeLessThan(64 * 1024, "the 2 MB pixel block is never read");
        var readsAfterCount = stream.Reads;

        PROPVARIANT pv = default;
        var key = CameraModel;
        store.GetValue(&key, &pv).ShouldBe(S_OK);
        PropVariantClear(&pv);
        PROPERTYKEY at = default;
        store.GetAt(0, &at).ShouldBe(S_OK);

        stream.Reads.ShouldBe(readsAfterCount, "the header is read once");
    }

    [Fact]
    public void AFitsEnumeratesExactlyTheKeysThatHaveAValue()
    {
        var store = Store(FitsBytes(32, 24, 16, ("INSTRUME", "'ZWO ASI533MC Pro'"), ("EXPTIME", "300.0"),
            ("OBJECT", "'M 31'"), ("FILTER", "'Ha'"), ("IMAGETYP", "'Light'")));

        var keys = Enumerate(store);

        keys.ShouldBe([CameraModel, CameraManufacturer, ExposureTime, Subject, Keywords, Dimensions, HorizontalSize,
            VerticalSize, BitDepth, FrameWidth, FrameHeight], ignoreOrder: true);
        uint count = 0;
        store.GetCount(&count).ShouldBe(S_OK);
        count.ShouldBe((uint)keys.Count);
        PROPERTYKEY past = default;
        store.GetAt(count, &past).ShouldBe(E_INVALIDARG);
    }

    [Fact]
    public void AFitsAnswersEachKeyWithItsVariantType()
    {
        var store = Store(FitsBytes(32, 24, 16,
            ("INSTRUME", "'ZWO ASI533MC Pro'"), ("TELESCOP", "'Sky-Watcher Esprit 100'"),
            ("EXPTIME", "300.0"), ("FOCALLEN", "550.0"), ("APTDIA", "100"), ("ISOSPEED", "800"),
            ("DATE-OBS", "'2026-11-01T21:30:15'"), ("OBJECT", "'M 31'"), ("FILTER", "'Ha'"), ("IMAGETYP", "'Light'"),
            ("OBSERVER", "'Ada Lovelace'"), ("SWCREATE", "'N.I.N.A. 3.1'")));

        String(store, CameraModel).ShouldBe("ZWO ASI533MC Pro");
        String(store, CameraManufacturer).ShouldBe("ZWO");
        String(store, LensModel).ShouldBe("Sky-Watcher Esprit 100");
        String(store, LensManufacturer).ShouldBe("Sky-Watcher");
        Value(store, ExposureTime, VT_R8, pv => BitConverter.Int64BitsToDouble((long)pv.data)).ShouldBe(300.0);
        Value(store, FocalLength, VT_R8, pv => BitConverter.Int64BitsToDouble((long)pv.data)).ShouldBe(550.0);
        Value(store, FNumber, VT_R8, pv => BitConverter.Int64BitsToDouble((long)pv.data)).ShouldBe(5.5);
        Value(store, IsoSpeed, VT_UI2, pv => (ushort)pv.data).ShouldBe((ushort)800);
        Value(store, DateTaken, VT_FILETIME, pv => (long)pv.data)
            .ShouldBe(new DateTimeOffset(2026, 11, 1, 21, 30, 15, TimeSpan.Zero).ToFileTime());
        String(store, Subject).ShouldBe("M 31");
        Vector(store, Keywords).ShouldBe(["Ha", "Light"]);
        Vector(store, Author).ShouldBe(["Ada Lovelace"]);
        String(store, ApplicationName).ShouldBe("N.I.N.A. 3.1");
        String(store, Dimensions).ShouldBe("32 x 24");
        Value(store, HorizontalSize, VT_UI4, pv => (uint)pv.data).ShouldBe(32u);
        Value(store, VerticalSize, VT_UI4, pv => (uint)pv.data).ShouldBe(24u);
        Value(store, BitDepth, VT_UI4, pv => (uint)pv.data).ShouldBe(16u);
        Value(store, FrameWidth, VT_UI4, pv => (uint)pv.data).ShouldBe(32u);
        Value(store, FrameHeight, VT_UI4, pv => (uint)pv.data).ShouldBe(24u);
    }

    [Fact]
    public void ASerAnswersItsDurationInHundredNanosecondsAndItsRateInFramesPerThousandSeconds()
    {
        var start = new DateTime(2026, 11, 1, 22, 0, 0, DateTimeKind.Utc);
        var store = Store(SerBytes(frames: 101, start, TimeSpan.FromMilliseconds(20)));

        // 100 intervals of 20 ms.
        Value(store, Duration, VT_UI8, pv => (long)pv.data).ShouldBe(TimeSpan.FromSeconds(2).Ticks);
        // 100 intervals over 2 s is 50 frames per second, which is 50000 frames per 1000 s.
        Value(store, FrameRate, VT_UI4, pv => (uint)pv.data).ShouldBe(50000u);
        String(store, CameraModel).ShouldBe("ZWO ASI662MC");
        Vector(store, Author).ShouldBe(["Grace Hopper"]);
        Value(store, FrameWidth, VT_UI4, pv => (uint)pv.data).ShouldBe(16u);
    }

    [Fact]
    public void AKeyTheStoreDoesNotHoldIsEmptyWithSOk()
    {
        var store = Store(FitsBytes(32, 24, 16));

        foreach (var key in new[] { ExposureTime, Subject, Keywords, Duration, FrameRate, IsoSpeed })
        {
            var k = key;
            PROPVARIANT pv = default;
            pv.vt = 99;
            store.GetValue(&k, &pv).ShouldBe(S_OK);
            pv.vt.ShouldBe(VT_EMPTY);
        }

        var unknown = new PROPERTYKEY(Guid.NewGuid(), 7);
        PROPVARIANT other = default;
        store.GetValue(&unknown, &other).ShouldBe(S_OK);
        other.vt.ShouldBe(VT_EMPTY);
    }

    [Fact]
    public void TheStoreRefusesEveryWrite()
    {
        var store = Store(FitsBytes(32, 24, 16));
        var key = CameraModel;
        PROPVARIANT pv = default;

        store.SetValue(&key, &pv).ShouldBe(STG_E_ACCESSDENIED);
        store.Commit().ShouldBe(STG_E_ACCESSDENIED);
        store.IsPropertyWritable(&key).ShouldBe(S_FALSE);
    }

    [Fact]
    public void JunkBytesGiveAnEmptyStoreNotAFailure()
    {
        var junk = new byte[10_000];
        new Random(7).NextBytes(junk);
        var store = Store(junk);

        uint count = 99;
        store.GetCount(&count).ShouldBe(S_OK);
        count.ShouldBe(0u);
        var key = CameraModel;
        PROPVARIANT pv = default;
        store.GetValue(&key, &pv).ShouldBe(S_OK);
        pv.vt.ShouldBe(VT_EMPTY);
    }

    [Fact]
    public void AStreamThatFailsGivesAnEmptyStoreNotAnExceptionAcrossTheBoundary()
    {
        var store = new AstroPropertyStore();
        store.Initialize(new SeekableStream(FitsBytes(32, 24, 16, ("INSTRUME", "'ZWO ASI533MC Pro'")), failReads: true), 0)
            .ShouldBe(S_OK);

        uint count = 99;
        Should.NotThrow(() => store.GetCount(&count).ShouldBe(S_OK));
        count.ShouldBe(0u);
    }

    [Fact]
    public void AskingBeforeInitializeIsUnexpected()
    {
        var store = new AstroPropertyStore();
        uint count = 0;

        store.GetCount(&count).ShouldBe(E_UNEXPECTED);
    }

    [Fact]
    public void DllGetClassObjectServesBothClsidsAndRefusesAnyOther()
    {
        delegate* unmanaged<Guid*, Guid*, nint*, int> getClassObject = &Exports.DllGetClassObject;
        var factoryIid = new Guid("00000001-0000-0000-C000-000000000046");

        foreach (var clsid in new[] { ThumbnailRenderer.ShellExtensionClsid, ThumbnailRenderer.PropertyHandlerClsid })
        {
            var c = clsid;
            nint factory = 0;
            getClassObject(&c, &factoryIid, &factory).ShouldBe(S_OK);
            factory.ShouldNotBe(0);
            Marshal.Release(factory);
        }

        var other = Guid.NewGuid();
        nint none = 1;
        getClassObject(&other, &factoryIid, &none).ShouldBe(CLASS_E_CLASSNOTAVAILABLE);
        none.ShouldBe(0);

        var notFactory = new Guid("0000000c-0000-0000-C000-000000000046");
        var handler = ThumbnailRenderer.PropertyHandlerClsid;
        getClassObject(&handler, &notFactory, &none).ShouldBe(E_NOINTERFACE);
    }

    [Fact]
    public void ThePropertyHandlerClsidIsStableAndDiffersFromTheThumbnailOne()
    {
        ThumbnailRenderer.PropertyHandlerClsid.ShouldBe(new Guid("22b95ef5-11fd-40eb-a4c9-5979daf5ffa5"));
        ThumbnailRenderer.PropertyHandlerClsid.ShouldNotBe(ThumbnailRenderer.ShellExtensionClsid);
    }

    private static AstroPropertyStore Store(byte[] bytes)
    {
        var store = new AstroPropertyStore();
        store.Initialize(new SeekableStream(bytes), 0).ShouldBe(S_OK);
        return store;
    }

    private static List<PROPERTYKEY> Enumerate(AstroPropertyStore store)
    {
        uint count = 0;
        store.GetCount(&count).ShouldBe(S_OK);
        var keys = new List<PROPERTYKEY>();
        for (uint i = 0; i < count; i++)
        {
            PROPERTYKEY key = default;
            store.GetAt(i, &key).ShouldBe(S_OK);
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>Reads one value, checks its VARTYPE, and clears it as the shell would so the test leaks nothing.</summary>
    private static T Value<T>(AstroPropertyStore store, PROPERTYKEY key, ushort expectedVt, Func<PROPVARIANT, T> read)
    {
        PROPVARIANT pv = default;
        store.GetValue(&key, &pv).ShouldBe(S_OK);
        try
        {
            pv.vt.ShouldBe(expectedVt);
            return read(pv);
        }
        finally
        {
            PropVariantClear(&pv).ShouldBe(S_OK);
        }
    }

    private static string String(AstroPropertyStore store, PROPERTYKEY key)
        => Value(store, key, VT_LPWSTR, pv => Marshal.PtrToStringUni((nint)pv.data)!);

    private static string[] Vector(AstroPropertyStore store, PROPERTYKEY key)
        => Value(store, key, VT_VECTOR | VT_LPWSTR, pv =>
        {
            var count = (int)(uint)pv.data;
            var array = (nint*)pv.data2;
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(array[i])!).ToArray();
        });

    private static byte[] FitsBytes(int width, int height, int bitpix, params (string Key, string Value)[] cards)
    {
        var all = new List<(string, string)>
        {
            ("SIMPLE", "T"), ("BITPIX", bitpix.ToString()), ("NAXIS", "2"), ("NAXIS1", width.ToString()), ("NAXIS2", height.ToString()),
        };
        all.AddRange(cards);

        var header = new StringBuilder();
        foreach (var (key, value) in all)
        {
            header.Append($"{key,-8}= {value}".PadRight(80));
        }

        header.Append("END".PadRight(80));
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString().PadRight((header.Length + 2879) / 2880 * 2880));
        var padded = ((long)width * height * (Math.Abs(bitpix) / 8) + 2879) / 2880 * 2880;
        var bytes = new byte[headerBytes.Length + padded];
        headerBytes.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] SerBytes(int frames, DateTime startUtc, TimeSpan interval)
    {
        var header = SerHeader.Create(SerColorId.Mono, 16, 12, 16, frameCount: frames,
            observer: "Grace Hopper", instrument: "ZWO ASI662MC", telescope: "Celestron C14",
            dateTimeTicks: startUtc.Ticks, dateTimeUtcTicks: startUtc.Ticks);
        var frameBytes = (long)header.FrameSizeBytes;
        var bytes = new byte[SerHeader.Size + (frames * frameBytes) + (frames * 8L)];
        header.Write(bytes.AsSpan(0, SerHeader.Size));
        var trailer = SerHeader.Size + (frames * frameBytes);
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)(trailer + (i * 8L)), 8), startUtc.Ticks + (i * interval.Ticks));
        }

        return bytes;
    }

    /// <summary>
    /// The shell's side of the boundary: a seekable stream that counts what is read, so a test can assert that
    /// nothing was, and how little was.
    /// </summary>
    private sealed class SeekableStream(byte[] bytes, bool failReads = false) : IStream
    {
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int E_FAIL = unchecked((int)0x80004005);

        private long _position;

        public int Reads { get; private set; }

        public int Seeks { get; private set; }

        public long BytesRead { get; private set; }

        public int Read(byte* pv, uint cb, uint* pcbRead)
        {
            Reads++;
            if (failReads)
            {
                return E_FAIL;
            }

            var take = (int)Math.Min(cb, (ulong)Math.Max(0, bytes.Length - _position));
            if (take > 0)
            {
                bytes.AsSpan((int)_position, take).CopyTo(new Span<byte>(pv, take));
            }

            _position += take;
            BytesRead += take;
            if (pcbRead is not null)
            {
                *pcbRead = (uint)take;
            }

            return take == 0 ? S_FALSE : S_OK;
        }

        public int Seek(long dlibMove, uint dwOrigin, ulong* plibNewPosition)
        {
            Seeks++;
            _position = dwOrigin switch
            {
                0 => dlibMove,
                1 => _position + dlibMove,
                _ => bytes.Length + dlibMove,
            };
            if (plibNewPosition is not null)
            {
                *plibNewPosition = (ulong)_position;
            }

            return S_OK;
        }

        public int Write(byte* pv, uint cb, uint* pcbWritten) => E_NOTIMPL;

        public int SetSize(ulong libNewSize) => E_NOTIMPL;

        public int CopyTo(nint pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten) => E_NOTIMPL;

        public int Commit(uint grfCommitFlags) => E_NOTIMPL;

        public int Revert() => E_NOTIMPL;

        public int LockRegion(ulong libOffset, ulong cb, uint dwLockType) => E_NOTIMPL;

        public int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) => E_NOTIMPL;

        public int Stat(void* pstatstg, uint grfStatFlag) => E_NOTIMPL;

        public int Clone(nint* ppstm) => E_NOTIMPL;
    }
}
