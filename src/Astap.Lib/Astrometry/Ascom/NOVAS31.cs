using Astap.Lib.Astrometry.NOVA;

namespace Astap.Lib.Astrometry.Ascom;

public class NOVAS31 : DynamicComObject
{
    public NOVAS31() : base("ASCOM.Astrometry.NOVAS.NOVAS31") { }

    public double? DeltaT(double jd) => _comObject?.DeltaT(jd) is double deltaT ? deltaT : default;

    public short SiderealTime(double intPart, double fraction, double deltaT, GstType gstType, Method method, Accuracy accuracy, ref double gst)
        => (_comObject?.SiderealTime(intPart, fraction, deltaT, gstType, method, accuracy, ref gst)) is short ret
            ? ret
            : short.MaxValue;
}
