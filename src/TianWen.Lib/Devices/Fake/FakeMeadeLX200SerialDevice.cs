using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200SerialDevice(bool isOpen, bool isSouthPole, Encoding encoding, TimeProvider timeProvider) : ISerialDevice
{
    private readonly AlignmentMode _alignmentMode = AlignmentMode.GermanPolar;
    private bool _isTracking = false;
    private int _alignmentStars = 0;
    private bool _highPrecision = false;
    // start pointing to the celestial pole
    private double _ra = 0;
    private double _dec = isSouthPole ? -90 : 90;
    private double _longitude = isSouthPole ? 146.78d : 110.29d;
    private double _latitude = isSouthPole ? -37.81d : 25.28;

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
                _responseBuffer.AppendFormat("{0}#", _highPrecision ? HoursToHMS(_ra) : HoursToHMT(_ra));
                return true;

            case ":GD#":
                _responseBuffer.AppendFormat("{0}#", _highPrecision ? DegreesToDMS(_dec, withPlus: false) : DegreesToDM(_dec));
                return true;

            case ":GS#":
                _responseBuffer.AppendFormat("{0}#", HoursToHMS(Transform.LocalSiderealTime(timeProvider.GetUtcNow(), _longitude)));
                return true;

            case ":U#":
                _highPrecision = !_highPrecision;
                return true;

            default:
                return false;
        }
    }

    private static string DegreesToDM(double degrees)
    {
        var dms = DegreesToDMS(degrees, withPlus: false);

        return dms[..dms.LastIndexOf(':')];
    }
}
