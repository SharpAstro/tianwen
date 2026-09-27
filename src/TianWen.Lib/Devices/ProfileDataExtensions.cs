using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Web;

namespace TianWen.Lib.Devices;

/// <summary>
/// Extension block helpers for <see cref="ProfileData"/>.
/// </summary>
public static class ProfileDataExtensions
{
    extension(ProfileData profile)
    {
        /// <summary>The site the profile stores, or null when it stores none.</summary>
        public SiteCoordinates? Site => SiteCoordinates.From(profile.SiteLatitude, profile.SiteLongitude, profile.SiteElevation);

        /// <summary>
        /// Mirrors a run's inferred backlash (its per-focuser EWMAs) into the matching focuser URIs, so the next run
        /// starts from last night's value: <c>focuserBacklashIn</c> / <c>focuserBacklashOut</c>. Each estimate is keyed by
        /// the focuser device URI the run used; only OTAs whose focuser matches one of those keys are touched, and every
        /// other query key on the URI (filter slot names, focus offsets, transport keys) is kept. Applied at a run's end by
        /// the GUI and the node alike (P3 part 3 of docs/plans/hardware-in-the-server.md, #930).
        /// </summary>
        /// <returns><c>(Updated, true)</c> when at least one URI changed; the profile unchanged and false otherwise.</returns>
        public (ProfileData Updated, bool Changed) WithBacklashEstimates(IReadOnlyDictionary<Uri, Astrometry.Focus.BacklashEstimateRecord> estimates)
        {
            if (estimates.Count == 0)
            {
                return (profile, false);
            }

            var changed = false;
            var newOtas = new OTAData[profile.OTAs.Length];
            for (var i = 0; i < profile.OTAs.Length; i++)
            {
                var ota = profile.OTAs[i];
                if (ota.Focuser is { } focUri && estimates.TryGetValue(focUri, out var record))
                {
                    var updatedFocuser = focUri.WithQueryValues(
                        (DeviceQueryKey.FocuserBacklashIn.Key, record.EwmaIn.ToString(CultureInfo.InvariantCulture)),
                        (DeviceQueryKey.FocuserBacklashOut.Key, record.EwmaOut.ToString(CultureInfo.InvariantCulture)));
                    if (updatedFocuser != focUri)
                    {
                        ota = ota with { Focuser = updatedFocuser };
                        changed = true;
                    }
                }
                newOtas[i] = ota;
            }

            return changed ? (profile with { OTAs = [.. newOtas] }, true) : (profile, false);
        }

        /// <summary>
        /// The profile with <paramref name="oldUri"/> replaced by <paramref name="newUri"/> in whichever slot it occupies
        /// (mount, guider, guider camera or focuser, weather, or an OTA's slots), matched by device, so a changed query
        /// still finds it. The mount keeps the query keys it had that <paramref name="newUri"/> does not set (its site,
        /// for one).
        /// </summary>
        public ProfileData ReplaceDeviceUri(Uri oldUri, Uri newUri)
        {
            var data = profile;
            if (DeviceBase.SameDevice(data.Mount, oldUri))
            {
                var baseQuery = HttpUtility.ParseQueryString(data.Mount.Query);
                var newQuery = HttpUtility.ParseQueryString(newUri.Query);
                foreach (string? key in newQuery)
                {
                    if (key is not null)
                    {
                        baseQuery[key] = newQuery[key];
                    }
                }
                data = data with { Mount = new UriBuilder(newUri) { Query = baseQuery.ToString() }.Uri };
            }
            if (DeviceBase.SameDevice(data.Guider, oldUri))
            {
                data = data with { Guider = newUri };
            }
            if (DeviceBase.SameDevice(data.GuiderCamera, oldUri))
            {
                data = data with { GuiderCamera = newUri };
            }
            if (DeviceBase.SameDevice(data.GuiderFocuser, oldUri))
            {
                data = data with { GuiderFocuser = newUri };
            }
            if (DeviceBase.SameDevice(data.Weather, oldUri))
            {
                data = data with { Weather = newUri };
            }

            for (var i = 0; i < data.OTAs.Length; i++)
            {
                var ota = data.OTAs[i];
                var changed = false;

                if (DeviceBase.SameDevice(ota.Camera, oldUri)) { ota = ota with { Camera = newUri }; changed = true; }
                if (DeviceBase.SameDevice(ota.Focuser, oldUri)) { ota = ota with { Focuser = newUri }; changed = true; }
                if (DeviceBase.SameDevice(ota.FilterWheel, oldUri)) { ota = ota with { FilterWheel = newUri }; changed = true; }
                if (DeviceBase.SameDevice(ota.Cover, oldUri)) { ota = ota with { Cover = newUri }; changed = true; }

                if (changed)
                {
                    data = data with { OTAs = data.OTAs.SetItem(i, ota) };
                }
            }

            return data;
        }

        /// <summary>The profile with <paramref name="site"/> as its site, elevation included.</summary>
        public ProfileData WithSite(SiteCoordinates site)
            => profile with { SiteLatitude = site.Latitude, SiteLongitude = site.Longitude, SiteElevation = site.Elevation };

        /// <summary>
        /// One-shot migration of site coordinates from the legacy Mount URI query string
        /// (<c>?latitude=…&amp;longitude=…&amp;elevation=…</c>) into <see cref="ProfileData.SiteLatitude"/> etc. Returns the
        /// updated profile and whether anything changed. When the profile already has a site the URI query is ignored:
        /// the profile wins for migration. Applied by every discovery's reconcile
        /// (<see cref="DeviceDiscoveryExtensions.ReconcileStoredProfile"/>), which the node runs on every discovery, the
        /// one a client asks for at its start included.
        /// </summary>
        public (ProfileData Data, bool Changed) MigrateSiteFromMountUri()
        {
            if (profile.SiteLatitude is not null || profile.SiteLongitude is not null) return (profile, false);
            if (profile.Mount == NoneDevice.Instance.DeviceUri) return (profile, false);

            var query = HttpUtility.ParseQueryString(profile.Mount.Query);
            var latStr = query[DeviceQueryKey.Latitude.Key];
            var lonStr = query[DeviceQueryKey.Longitude.Key];
            var elevStr = query[DeviceQueryKey.Elevation.Key];

            if (latStr is null || lonStr is null
                || !double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon))
            {
                return (profile, false);
            }

            double? elev = elevStr is not null && double.TryParse(elevStr, CultureInfo.InvariantCulture, out var e) ? e : null;
            return (profile with { SiteLatitude = lat, SiteLongitude = lon, SiteElevation = elev }, true);
        }

        /// <summary>
        /// Captures a just-connected camera's sensor geometry (pixel size + dimensions) into the OTA that references it, so
        /// the planner can compute the sensor FOV (and therefore smart framing groups) offline later, before any device is
        /// connected. Returns the updated profile when something actually changed (so the caller persists), or
        /// <see langword="null"/> when the driver reports no usable geometry, the camera isn't part of any OTA, or the specs
        /// already match (connect is frequent; only a genuine change warrants a save). Pure transformation, applied on a
        /// camera's connect by the GUI and the node alike.
        /// </summary>
        public ProfileData? CaptureSensorSpecs(Uri cameraUri, ICameraDriver camera)
        {
            var pixelSize = camera.PixelSizeX;
            var sensorW = camera.CameraXSize;
            var sensorH = camera.CameraYSize;
            if (!(pixelSize > 0) || sensorW <= 0 || sensorH <= 0)
            {
                return null; // driver hasn't reported usable sensor geometry
            }

            var otas = profile.OTAs;
            for (var i = 0; i < otas.Length; i++)
            {
                var ota = otas[i];
                if (!DeviceBase.SameDevice(ota.Camera, cameraUri))
                {
                    continue;
                }

                // Already captured and unchanged -> nothing to persist.
                if (ota.CameraSensorWidthPx == sensorW
                    && ota.CameraSensorHeightPx == sensorH
                    && ota.CameraPixelSizeUm is { } existing && Math.Abs(existing - pixelSize) < 1e-6)
                {
                    return null;
                }

                var updated = ota with
                {
                    CameraPixelSizeUm = pixelSize,
                    CameraSensorWidthPx = sensorW,
                    CameraSensorHeightPx = sensorH,
                };
                return profile with { OTAs = otas.SetItem(i, updated) };
            }

            return null; // camera not assigned to any OTA
        }

        /// <summary>
        /// This profile, an edit made of <paramref name="based"/>, made again onto <paramref name="latest"/>: every field
        /// the edit changed takes the edit's value, and every other field the latest's, so a change made elsewhere since
        /// the edit was read survives it. What a client does when the node refuses an edit as made against a profile that
        /// has moved on (a 412, P6 of docs/plans/hardware-in-the-server.md, #936), in place of losing either change.
        /// <para>
        /// The telescopes are rebased one by one while all three agree on how many there are, since an edit of one
        /// telescope (its camera, its filters) must not undo a change to another; an edit that adds or removes one takes
        /// the edit's list whole, as an index no longer names the same telescope in both.
        /// </para>
        /// </summary>
        public ProfileData RebasedOnto(ProfileData based, ProfileData latest)
        {
            // A URI compared whole: Uri.Equals leaves out the fragment, which is a device's display name.
            static T Pick<T>(T based, T edited, T latest) =>
                (based is Uri a && edited is Uri b ? string.Equals(a.OriginalString, b.OriginalString, StringComparison.Ordinal) : EqualityComparer<T>.Default.Equals(based, edited))
                    ? latest : edited;

            ImmutableArray<OTAData> otas;
            if (based.OTAs.SequenceEqual(profile.OTAs))
            {
                otas = latest.OTAs;
            }
            else if (based.OTAs.Length == profile.OTAs.Length && latest.OTAs.Length == profile.OTAs.Length)
            {
                var rebased = ImmutableArray.CreateBuilder<OTAData>(profile.OTAs.Length);
                for (var i = 0; i < profile.OTAs.Length; i++)
                {
                    rebased.Add(Pick(based.OTAs[i], profile.OTAs[i], latest.OTAs[i]));
                }
                otas = rebased.MoveToImmutable();
            }
            else
            {
                otas = profile.OTAs;
            }

            return new ProfileData(
                Mount: Pick(based.Mount, profile.Mount, latest.Mount),
                Guider: Pick(based.Guider, profile.Guider, latest.Guider),
                OTAs: otas,
                GuiderCamera: Pick(based.GuiderCamera, profile.GuiderCamera, latest.GuiderCamera),
                GuiderFocuser: Pick(based.GuiderFocuser, profile.GuiderFocuser, latest.GuiderFocuser),
                OAG_OTA_Index: Pick(based.OAG_OTA_Index, profile.OAG_OTA_Index, latest.OAG_OTA_Index),
                GuiderFocalLength: Pick(based.GuiderFocalLength, profile.GuiderFocalLength, latest.GuiderFocalLength),
                Weather: Pick(based.Weather, profile.Weather, latest.Weather),
                SiteLatitude: Pick(based.SiteLatitude, profile.SiteLatitude, latest.SiteLatitude),
                SiteLongitude: Pick(based.SiteLongitude, profile.SiteLongitude, latest.SiteLongitude),
                SiteElevation: Pick(based.SiteElevation, profile.SiteElevation, latest.SiteElevation),
                SiteTieBreaker: Pick(based.SiteTieBreaker, profile.SiteTieBreaker, latest.SiteTieBreaker),
                MountLimits: Pick(based.MountLimits, profile.MountLimits, latest.MountLimits));
        }

        /// <summary>
        /// A human-readable diff from this profile to <paramref name="after"/>, one tuple per device slot that changed, for
        /// logging a reconcile so a transport refresh and a user-config clobber are both visible. Null URIs render as
        /// <c>&lt;none&gt;</c>.
        /// </summary>
        public IEnumerable<(string Field, string Before, string After)> DiffTo(ProfileData after)
        {
            static string F(Uri? u) => u?.ToString() ?? "<none>";

            if (profile.Mount != after.Mount)
                yield return ("Mount", F(profile.Mount), F(after.Mount));
            if (profile.Guider != after.Guider)
                yield return ("Guider", F(profile.Guider), F(after.Guider));
            if (profile.GuiderCamera != after.GuiderCamera)
                yield return ("GuiderCamera", F(profile.GuiderCamera), F(after.GuiderCamera));
            if (profile.GuiderFocuser != after.GuiderFocuser)
                yield return ("GuiderFocuser", F(profile.GuiderFocuser), F(after.GuiderFocuser));
            if (profile.Weather != after.Weather)
                yield return ("Weather", F(profile.Weather), F(after.Weather));
            if (profile.Site != after.Site)
                yield return ("Site", profile.Site?.ToString() ?? "<none>", after.Site?.ToString() ?? "<none>");

            var maxOtas = Math.Max(profile.OTAs.Length, after.OTAs.Length);
            for (int i = 0; i < maxOtas; i++)
            {
                var b = i < profile.OTAs.Length ? (OTAData?)profile.OTAs[i] : null;
                var a = i < after.OTAs.Length ? (OTAData?)after.OTAs[i] : null;
                if (b is null && a is not null) yield return ($"OTA[{i}]", "<none>", "<added>");
                else if (a is null && b is not null) yield return ($"OTA[{i}]", "<present>", "<removed>");
                else if (b is { } bb && a is { } aa)
                {
                    if (bb.Camera != aa.Camera) yield return ($"OTA[{i}].Camera", F(bb.Camera), F(aa.Camera));
                    if (bb.Cover != aa.Cover) yield return ($"OTA[{i}].Cover", F(bb.Cover), F(aa.Cover));
                    if (bb.Focuser != aa.Focuser) yield return ($"OTA[{i}].Focuser", F(bb.Focuser), F(aa.Focuser));
                    if (bb.FilterWheel != aa.FilterWheel) yield return ($"OTA[{i}].FilterWheel", F(bb.FilterWheel), F(aa.FilterWheel));
                }
            }
        }

        /// <summary>
        /// True if any URI slot in the profile references a fake device. Used at
        /// startup to opt the first device discovery into <c>IncludeFake: true</c> so
        /// a profile set up with fake devices can actually connect without the user
        /// having to press Shift+Discover first.
        /// </summary>
        /// <remarks>
        /// Fake device URIs use the device type as the scheme and <c>FakeDevice</c> as
        /// the host (e.g. <c>Mount://FakeDevice/fake-sky</c>), not a dedicated
        /// <c>fakedevice://</c> scheme; see <c>EquipmentActions.TryDeviceFromUri</c>.
        /// </remarks>
        public bool ReferencesAnyFakeDevice
        {
            get
            {
                if (IsFake(profile.Mount)) return true;
                if (IsFake(profile.Guider)) return true;
                if (IsFake(profile.GuiderCamera)) return true;
                if (IsFake(profile.GuiderFocuser)) return true;
                if (IsFake(profile.Weather)) return true;

                foreach (var ota in profile.OTAs)
                {
                    if (IsFake(ota.Camera)) return true;
                    if (IsFake(ota.Cover)) return true;
                    if (IsFake(ota.Focuser)) return true;
                    if (IsFake(ota.FilterWheel)) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Yields every non-empty, non-<c>NoneDevice</c> device URI assigned in the profile,
        /// in a stable order: Mount, Guider, GuiderCamera, GuiderFocuser, Weather, then per-OTA
        /// Camera, Cover, Focuser, FilterWheel. Useful for bulk operations like "Connect All"
        /// or "Disconnect All" that need to walk the profile's full device set without
        /// duplicating the slot list at every call site.
        /// </summary>
        public IEnumerable<Uri> AssignedDeviceUris
        {
            get
            {
                if (IsAssigned(profile.Mount)) yield return profile.Mount;
                if (IsAssigned(profile.Guider)) yield return profile.Guider;
                if (IsAssigned(profile.GuiderCamera)) yield return profile.GuiderCamera;
                if (IsAssigned(profile.GuiderFocuser)) yield return profile.GuiderFocuser;
                if (IsAssigned(profile.Weather)) yield return profile.Weather;

                foreach (var ota in profile.OTAs)
                {
                    if (IsAssigned(ota.Camera)) yield return ota.Camera;
                    if (IsAssigned(ota.Cover)) yield return ota.Cover;
                    if (IsAssigned(ota.Focuser)) yield return ota.Focuser;
                    if (IsAssigned(ota.FilterWheel)) yield return ota.FilterWheel;
                }
            }
        }
    }

    private static bool IsFake(Uri? uri)
        => uri is not null
           && uri.Host.Equals("FakeDevice", StringComparison.OrdinalIgnoreCase);

    /// <remarks>
    /// <see cref="NotNullWhenAttribute"/> so a caller that has asked the question can yield the URI
    /// without re-asserting it: the six slots below are nullable only because a profile may leave
    /// them empty, and this predicate is the one place that is decided.
    /// </remarks>
    private static bool IsAssigned([NotNullWhen(true)] Uri? uri)
        => uri is not null && uri != NoneDevice.Instance.DeviceUri;
}
