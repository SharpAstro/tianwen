using System;

namespace Astap.Lib.Astrometry.NOVA;

public interface INOVAS31
{
    double? DeltaT(double jd);

    int? SiderealTime(double intPart, double fraction, double deltaT,
        GstType gstType,
        Method method,
        Accuracy accuracy, ref double siderealTime);

    public double SiderealTime(DateTimeOffset dateTimeOffset, double siteLongitude)
    {
        double gst = 0.0;

        var jd = dateTimeOffset.ToJulian();
        var intPart = Math.Floor(jd);
        var fraction = jd - intPart;
        if (DeltaT(jd) is not double deltaT)
        {
            throw new InvalidOperationException($"NOVAS 3.1 SiderealTime could not get delta T of Julian date {jd}");
        }
        var siderealTimeResult = SiderealTime(intPart, fraction, deltaT,
            GstType.GreenwichApparentSiderealTime,
            Method.EquinoxBased,
            Accuracy.Reduced, ref gst);

        if (siderealTimeResult != 0)
        {
            throw new InvalidOperationException($"NOVAS 3.1 SiderealTime returned: {siderealTimeResult} in SiderealTime");
        }

        // Allow for the longitude
        gst += siteLongitude / 360.0 * 24.0;

        // Reduce to the range 0 to 24 hours
        return Utils.ConditionRA(gst);
    }
}
