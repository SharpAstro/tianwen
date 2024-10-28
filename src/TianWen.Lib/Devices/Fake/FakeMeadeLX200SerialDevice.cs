using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200SerialDevice(bool isOpen, Encoding encoding, Transform initialPosition) : ISerialDevice
{
    private readonly AlignmentMode _alignmentMode = AlignmentMode.GermanPolar;
    private bool _isTracking = false;
    private int _alignmentStars = 0;
    private bool _highPrecision = false;
    // start pointing to the celestial pole
    private double _ra = initialPosition.RATopocentric;
    private double _dec = initialPosition.DECTopocentric;
    private double _targetRa = initialPosition.RATopocentric;
    private double _targetDec = initialPosition.DECTopocentric;
    private double _longitude = initialPosition.SiteLatitude;
    private double _latitude = initialPosition.SiteLatitude;

    // I/O properties
    private readonly StringBuilder _responseBuffer = new StringBuilder();
    private int _responsePointer = 0;

    public bool IsOpen { get; private set; } = isOpen;

    public Encoding Encoding { get; private set; } = encoding;

    public void Dispose() => TryClose();

    public bool TryClose()
    {
        IsOpen = false;
        return true;
    }

    public bool TryReadExactly(int count, [NotNullWhen(true)] out ReadOnlySpan<byte> message)
    {
        if (_responsePointer + count <= _responseBuffer.Length)
        {
            var chars = new char[count];
            _responseBuffer.CopyTo(_responsePointer, chars, count);
            _responsePointer += count;

            ClearBufferIfEmpty();

            message = Encoding.GetBytes(chars);
            return true;
        }

        message = null;
        return false;
    }

    public bool TryReadTerminated([NotNullWhen(true)] out ReadOnlySpan<byte> message, ReadOnlySpan<byte> terminators)
    {
        var chars = new char[_responseBuffer.Length - _responsePointer];
        var terminatorChars = Encoding.GetString(terminators);

        int i = 0;
        while (_responsePointer < _responseBuffer.Length)
        {
            var @char = _responseBuffer[_responsePointer++];

            if (terminatorChars.Contains(@char))
            {
                ClearBufferIfEmpty();

                message = Encoding.GetBytes(chars[0..i]);
                return true;
            }
            else
            {
                chars[i++] = @char;
            }
        }

        message = null;
        return false;
    }

    private void ClearBufferIfEmpty()
    {
        if (_responsePointer == _responseBuffer.Length)
        {
            _responseBuffer.Clear();
            _responsePointer = 0;
        }
    }

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        var dataStr = Encoding.GetString(data);

        switch (dataStr)
        {
            case ":GVP#":
                _responseBuffer.Append("Fake LX200 Mount#");
                return true;

            case ":GW#":
                _responseBuffer.AppendFormat("{0}{1}{2:0}",
                    _alignmentMode switch { AlignmentMode.GermanPolar => 'G', _ => '?' },
                    _isTracking ? 'T' : 'N',
                    _alignmentStars
                );
                return true;

            case ":GVN#":
                _responseBuffer.Append("A4s4#");
                return true;

            case ":GR#":
                RespondHMS(_ra);
                return true;

            case ":Gr#":
                RespondHMS(_targetRa);
                return true;

            case ":GD#":
                RespondDMS(_dec);
                return true;

            case ":Gd#":
                RespondDMS(_targetDec);
                return true;

            case ":GS#":
                _responseBuffer.AppendFormat("{0}#", HoursToHMS(SiderealTime));
                return true;

            case ":U#":
                _highPrecision = !_highPrecision;
                return true;

            default:
                return false;
        }

        void RespondHMS(double ra) => _responseBuffer.AppendFormat("{0}#", _highPrecision ? HoursToHMS(ra) : HoursToHMT(ra));

        void RespondDMS(double dec) => _responseBuffer.AppendFormat("{0}#", _highPrecision ? DegreesToDMS(dec, withPlus: false, degreeSign: '\xdf') : DegreesToDM(dec));
    }

    private double SiderealTime => Transform.LocalSiderealTime(initialPosition.TimeProvider.GetUtcNow(), _longitude);

    private static string DegreesToDM(double degrees)
    {
        var dms = DegreesToDMS(degrees, withPlus: false);

        return dms[..dms.LastIndexOf(':')];
    }
}
