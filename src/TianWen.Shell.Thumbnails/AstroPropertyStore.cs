using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TianWen.Lib.Imaging;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// The read-only property handler behind Explorer's detail columns for FITS and SER files (#2429): initialised
    /// once with the file's stream, asked for its properties, then released. One instance per request, in the
    /// shell's COM surrogate.
    /// <para>
    /// All the reading is <see cref="ExplorerFileProperties"/> in TianWen.Lib; this class only marshals its fields
    /// into <c>PROPERTYKEY</c>s and <c>PROPVARIANT</c>s. It reads the header through a seeking <see cref="ComStream"/>
    /// and never the pixel block, and no exception and no pixel read crosses the COM boundary: a file it cannot
    /// make sense of is an empty store.
    /// </para>
    /// </summary>
    [GeneratedComClass]
    [SupportedOSPlatform("windows")]
    internal sealed unsafe partial class AstroPropertyStore : IInitializeWithStream, IPropertyStore, IPropertyStoreCapabilities
    {
        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        private const int E_INVALIDARG = unchecked((int)0x80070057);
        private const int E_POINTER = unchecked((int)0x80004003);
        private const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
        private const int HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED = unchecked((int)0x800700B7);

        // VARTYPEs (wtypes.h).
        private const ushort VT_EMPTY = 0;
        private const ushort VT_R8 = 5;
        private const ushort VT_UI2 = 18;
        private const ushort VT_UI4 = 19;
        private const ushort VT_UI8 = 21;
        private const ushort VT_LPWSTR = 31;
        private const ushort VT_FILETIME = 64;
        private const ushort VT_VECTOR = 0x1000;

        // propkey.h. The slot order below is the enumeration order.
        private static readonly Guid FmtPhoto = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
        private static readonly Guid FmtSummary = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
        private static readonly Guid FmtImage = new("6444048F-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtMedia = new("64440490-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtVideo = new("64440491-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtLensModel = new("E1277516-2B5F-4869-89B1-2E585BD38B7A");
        private static readonly Guid FmtLensMaker = new("E6DDCAF7-29C5-4F0A-9A68-D19412EC7090");

        private const int SlotCameraModel = 0;
        private const int SlotCameraManufacturer = 1;
        private const int SlotLensModel = 2;
        private const int SlotLensManufacturer = 3;
        private const int SlotExposureTime = 4;
        private const int SlotFocalLength = 5;
        private const int SlotFNumber = 6;
        private const int SlotIsoSpeed = 7;
        private const int SlotDateTaken = 8;
        private const int SlotSubject = 9;
        private const int SlotKeywords = 10;
        private const int SlotAuthor = 11;
        private const int SlotApplicationName = 12;
        private const int SlotDimensions = 13;
        private const int SlotHorizontalSize = 14;
        private const int SlotVerticalSize = 15;
        private const int SlotBitDepth = 16;
        private const int SlotFrameWidth = 17;
        private const int SlotFrameHeight = 18;
        private const int SlotDuration = 19;
        private const int SlotFrameRate = 20;

        internal static readonly PROPERTYKEY[] Keys =
        [
            new(FmtPhoto, 272),       // System.Photo.CameraModel
            new(FmtPhoto, 271),       // System.Photo.CameraManufacturer
            new(FmtLensModel, 100),   // System.Photo.LensModel
            new(FmtLensMaker, 100),   // System.Photo.LensManufacturer
            new(FmtPhoto, 33434),     // System.Photo.ExposureTime
            new(FmtPhoto, 37386),     // System.Photo.FocalLength
            new(FmtPhoto, 33437),     // System.Photo.FNumber
            new(FmtPhoto, 34855),     // System.Photo.ISOSpeed
            new(FmtPhoto, 36867),     // System.Photo.DateTaken
            new(FmtSummary, 3),       // System.Subject
            new(FmtSummary, 5),       // System.Keywords
            new(FmtSummary, 4),       // System.Author
            new(FmtSummary, 18),      // System.ApplicationName
            new(FmtImage, 13),        // System.Image.Dimensions
            new(FmtImage, 3),         // System.Image.HorizontalSize
            new(FmtImage, 4),         // System.Image.VerticalSize
            new(FmtImage, 7),         // System.Image.BitDepth
            new(FmtVideo, 3),         // System.Video.FrameWidth
            new(FmtVideo, 4),         // System.Video.FrameHeight
            new(FmtMedia, 3),         // System.Media.Duration
            new(FmtVideo, 6),         // System.Video.FrameRate
        ];

        /// <summary>FILETIME ticks (100 ns since 1601) of the first instant a FILETIME can hold.</summary>
        private const long FileTimeEpochTicks = 504911232000000000;

        // A lock because the store is declared threading model Both; it is held only across one header read, and
        // the shell never calls from a render thread.
        private readonly Lock _gate = new();

        private IStream? _stream;
        private bool _initialized;
        private ExplorerFileProperties? _props;
        private int[] _present = [];

        public int Initialize(IStream pstream, uint grfMode)
        {
            lock (_gate)
            {
                if (_initialized)
                {
                    return HRESULT_FROM_WIN32_ERROR_ALREADY_INITIALIZED;
                }

                // Takes the stream and reads nothing: over a cloud placeholder the first read is the download,
                // and the shell may never ask for a value.
                _stream = pstream;
                _initialized = true;
                return S_OK;
            }
        }

        public int GetCount(uint* cProps)
        {
            if (cProps is null)
            {
                return E_POINTER;
            }

            *cProps = 0;
            if (!TryLoad(out _))
            {
                return E_UNEXPECTED;
            }

            *cProps = (uint)_present.Length;
            return S_OK;
        }

        public int GetAt(uint iProp, PROPERTYKEY* pkey)
        {
            if (pkey is null)
            {
                return E_POINTER;
            }

            *pkey = default;
            if (!TryLoad(out _))
            {
                return E_UNEXPECTED;
            }

            if (iProp >= (uint)_present.Length)
            {
                return E_INVALIDARG;
            }

            *pkey = Keys[_present[iProp]];
            return S_OK;
        }

        public int GetValue(PROPERTYKEY* key, PROPVARIANT* pv)
        {
            if (key is null || pv is null)
            {
                return E_POINTER;
            }

            *pv = default;
            if (!TryLoad(out var props))
            {
                return E_UNEXPECTED;
            }

            var slot = -1;
            for (var i = 0; i < Keys.Length; i++)
            {
                if (Keys[i].pid == key->pid && Keys[i].fmtid == key->fmtid)
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0 || !Has(props, slot))
            {
                // A key the store does not hold is VT_EMPTY with S_OK, as the shell expects.
                return S_OK;
            }

            try
            {
                Fill(props, slot, pv);
            }
            catch (Exception)
            {
                // Out of memory for a string: nothing is half-written, the value is simply absent.
                *pv = default;
            }

            return S_OK;
        }

        public int SetValue(PROPERTYKEY* key, PROPVARIANT* propvar) => STG_E_ACCESSDENIED;

        public int Commit() => STG_E_ACCESSDENIED;

        public int IsPropertyWritable(PROPERTYKEY* key) => S_FALSE;

        /// <summary>
        /// Reads the header on the first property call and hands the stream back at once, so the shell's end
        /// of a cloud placeholder is not held for the life of a process that never collects. False before a
        /// successful <see cref="Initialize"/>.
        /// </summary>
        private bool TryLoad(out ExplorerFileProperties props)
        {
            lock (_gate)
            {
                if (_props is null)
                {
                    if (_stream is not { } stream)
                    {
                        props = ExplorerFileProperties.Empty;
                        return false;
                    }

                    ExplorerFileProperties read;
                    try
                    {
                        read = ExplorerFileProperties.Read(new ComStream(stream));
                    }
                    catch (Exception)
                    {
                        read = ExplorerFileProperties.Empty;
                    }

                    ReleaseStream();
                    _props = read;
                    _present = Enumerable.Range(0, Keys.Length).Where(i => Has(read, i)).ToArray();
                }

                props = _props;
                return true;
            }
        }

        /// <summary>
        /// Drops the stream and releases the native reference behind it. The wrapper is a
        /// <see cref="ComObject"/> only across a real COM boundary; in-process callers pass a managed
        /// implementation and there is nothing to release.
        /// </summary>
        private void ReleaseStream()
        {
            var stream = _stream;
            _stream = null;

            if ((object?)stream is ComObject com)
            {
                com.FinalRelease();
            }
        }

        private static bool Has(ExplorerFileProperties p, int slot) => slot switch
        {
            SlotCameraModel => p.CameraModel is not null,
            SlotCameraManufacturer => p.CameraMaker is not null,
            SlotLensModel => p.LensModel is not null,
            SlotLensManufacturer => p.LensMaker is not null,
            SlotExposureTime => p.ExposureSeconds is { } e && double.IsFinite(e),
            SlotFocalLength => p.FocalLengthMm is { } f && double.IsFinite(f),
            SlotFNumber => p.FNumber is { } n && double.IsFinite(n),
            SlotIsoSpeed => p.IsoSpeed is not null,
            SlotDateTaken => p.DateTaken is { } d && d.UtcTicks >= FileTimeEpochTicks,
            SlotSubject => p.Subject is not null,
            SlotKeywords => !p.Tags.IsDefaultOrEmpty,
            SlotAuthor => p.Author is not null,
            SlotApplicationName => p.ProgramName is not null,
            SlotDimensions => p.Width is not null && p.Height is not null,
            SlotHorizontalSize or SlotFrameWidth => p.Width is not null,
            SlotVerticalSize or SlotFrameHeight => p.Height is not null,
            SlotBitDepth => p.BitDepth is not null,
            SlotDuration => p.Duration is { Ticks: > 0 },
            SlotFrameRate => p.FrameRate is { } r && double.IsFinite(r) && r > 0 && r * 1000 < uint.MaxValue,
            _ => false,
        };

        private static void Fill(ExplorerFileProperties p, int slot, PROPVARIANT* pv)
        {
            switch (slot)
            {
                case SlotCameraModel: SetString(pv, p.CameraModel ?? ""); break;
                case SlotCameraManufacturer: SetString(pv, p.CameraMaker ?? ""); break;
                case SlotLensModel: SetString(pv, p.LensModel ?? ""); break;
                case SlotLensManufacturer: SetString(pv, p.LensMaker ?? ""); break;
                case SlotExposureTime: SetDouble(pv, p.ExposureSeconds.GetValueOrDefault()); break;
                case SlotFocalLength: SetDouble(pv, p.FocalLengthMm.GetValueOrDefault()); break;
                case SlotFNumber: SetDouble(pv, p.FNumber.GetValueOrDefault()); break;
                case SlotIsoSpeed: pv->vt = VT_UI2; pv->data = p.IsoSpeed.GetValueOrDefault(); break;
                case SlotDateTaken:
                    pv->vt = VT_FILETIME;
                    pv->data = (ulong)(p.DateTaken.GetValueOrDefault().UtcTicks - FileTimeEpochTicks);
                    break;
                case SlotSubject: SetString(pv, p.Subject ?? ""); break;
                case SlotKeywords: SetStringVector(pv, [.. p.Tags]); break;
                case SlotAuthor: SetStringVector(pv, [p.Author ?? ""]); break;
                case SlotApplicationName: SetString(pv, p.ProgramName ?? ""); break;
                case SlotDimensions:
                    SetString(pv, string.Create(CultureInfo.InvariantCulture, $"{p.Width} x {p.Height}"));
                    break;
                case SlotHorizontalSize or SlotFrameWidth: SetUInt32(pv, p.Width.GetValueOrDefault()); break;
                case SlotVerticalSize or SlotFrameHeight: SetUInt32(pv, p.Height.GetValueOrDefault()); break;
                case SlotBitDepth: SetUInt32(pv, p.BitDepth.GetValueOrDefault()); break;
                case SlotDuration: pv->vt = VT_UI8; pv->data = (ulong)p.Duration.GetValueOrDefault().Ticks; break;
                // Frames per 1000 seconds, which is System.Video.FrameRate's unit.
                case SlotFrameRate: SetUInt32(pv, (uint)Math.Round(p.FrameRate.GetValueOrDefault() * 1000)); break;
                default: pv->vt = VT_EMPTY; break;
            }
        }

        private static void SetDouble(PROPVARIANT* pv, double value)
        {
            pv->vt = VT_R8;
            pv->data = (ulong)BitConverter.DoubleToInt64Bits(value);
        }

        private static void SetUInt32(PROPVARIANT* pv, uint value)
        {
            pv->vt = VT_UI4;
            pv->data = value;
        }

        // CoTaskMemAlloc'd, as PropVariantClear frees them with CoTaskMemFree.
        private static void SetString(PROPVARIANT* pv, string value)
        {
            var p = Marshal.StringToCoTaskMemUni(value);
            pv->vt = VT_LPWSTR;
            pv->data = (ulong)p;
        }

        private static void SetStringVector(PROPVARIANT* pv, string[] values)
        {
            var array = (nint*)Marshal.AllocCoTaskMem(values.Length * sizeof(nint));
            var filled = 0;
            try
            {
                for (; filled < values.Length; filled++)
                {
                    array[filled] = Marshal.StringToCoTaskMemUni(values[filled]);
                }
            }
            catch
            {
                for (var i = 0; i < filled; i++)
                {
                    Marshal.FreeCoTaskMem(array[i]);
                }

                Marshal.FreeCoTaskMem((nint)array);
                throw;
            }

            pv->vt = VT_VECTOR | VT_LPWSTR;
            pv->data = (uint)values.Length;
            pv->data2 = (ulong)array;
        }
    }
}
