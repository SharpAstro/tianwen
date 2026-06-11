using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Internal seam exposing a fake mount's TRUE sky pointing -- the direction the OTA
/// physically points after hidden alignment errors (polar misalignment, cone error)
/// are applied -- as opposed to the believed (encoder-derived) pointing the public
/// <see cref="IMountDriver.GetRightAscensionAsync"/> / <see cref="IMountDriver.GetDeclinationAsync"/>
/// reads report. Mirrors reality: a mount's encoders only know where the mount
/// BELIEVES it is; the true pointing is only observable through a camera (plate
/// solving). Consumed exclusively by the fake camera / fake guider so their
/// synthesised frames render the sky the sensor would actually see, while every
/// other client (sky map marker, telemetry, sessions) sees the believed pointing
/// like it would on real hardware.
/// </summary>
internal interface IFakeTruePointingSource
{
    /// <summary>
    /// True sky pointing in the mount's NATIVE equatorial frame (same epoch the
    /// public position reads use, see <see cref="IMountDriver.EquatorialSystem"/>).
    /// RA in hours [0, 24), Dec in degrees [-90, 90]. Callers convert to J2000 via
    /// the same transform path used for public reads.
    /// </summary>
    ValueTask<(double Ra, double Dec)> GetTruePointingNativeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// J2000 convenience over <see cref="IFakeTruePointingSource"/> mirroring
/// <see cref="IMountDriver.GetRaDecJ2000Async(Transform, bool, CancellationToken)"/>
/// step for step (connected gate, NaN gate, epoch short-circuits, optional mount-UTC
/// refresh, shared <see cref="EquatorialFrameConversion.TopocentricToJ2000"/> tail) so
/// the believed and true pointing convert through the SAME path -- any divergence
/// would inject a spurious offset into the (true - believed) delta the fake camera
/// renders.
/// </summary>
internal static class FakeTruePointingSourceExtensions
{
    extension(IFakeTruePointingSource source)
    {
        /// <summary>
        /// True sky pointing converted to J2000, or null when unavailable (not a
        /// connected mount, native read NaN, unsupported equatorial system).
        /// </summary>
        /// <param name="transform">Caller-owned transform; reusing the same instance across
        /// calls avoids re-reading site coordinates. Not re-entrant if shared.</param>
        /// <param name="updateTime">If true, refresh the transform's time from the mount's
        /// UTC clock before converting (one extra hardware read).</param>
        public async ValueTask<(double RaJ2000, double DecJ2000)?> GetTruePointingJ2000Async(Transform transform, bool updateTime, CancellationToken cancellationToken)
        {
            if (source is not IMountDriver mount || !mount.Connected)
            {
                return null;
            }

            var (raNative, decNative) = await source.GetTruePointingNativeAsync(cancellationToken);
            if (double.IsNaN(raNative) || double.IsNaN(decNative))
            {
                return null;
            }

            // Mount already reports J2000 — no conversion needed, skip the Transform entirely.
            if (mount.EquatorialSystem == EquatorialCoordinateType.J2000)
            {
                return (raNative, decNative);
            }

            if (mount.EquatorialSystem != EquatorialCoordinateType.Topocentric)
            {
                return null;
            }

            if (updateTime)
            {
                if (await mount.TryGetUTCDateFromMountAsync(cancellationToken) is not { } utc)
                {
                    return null;
                }
                transform.DateTime = utc;
            }

            return EquatorialFrameConversion.TopocentricToJ2000(transform, raNative, decNative);
        }
    }
}
