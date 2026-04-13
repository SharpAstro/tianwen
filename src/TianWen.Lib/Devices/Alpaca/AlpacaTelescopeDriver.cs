using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaTelescopeDriver(AlpacaDevice device, IServiceProvider serviceProvider)
    : AlpacaDeviceDriverBase(device, serviceProvider), IMountDriver
{
    private List<TrackingSpeed> _trackingSpeeds = [];

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        CanSetTracking = await TryGetCapabilityAsync("cansettracking", cancellationToken);
        CanSetSideOfPier = await TryGetCapabilityAsync("cansetpierside", cancellationToken);
        CanPark = await TryGetCapabilityAsync("canpark", cancellationToken);
        CanUnpark = await TryGetCapabilityAsync("canunpark", cancellationToken);
        CanSetPark = await TryGetCapabilityAsync("cansetpark", cancellationToken);
        CanSlew = await TryGetCapabilityAsync("canslew", cancellationToken);
        CanSlewAsync = await TryGetCapabilityAsync("canslewasync", cancellationToken);
        CanSync = await TryGetCapabilityAsync("cansync", cancellationToken);
        CanPulseGuide = await TryGetCapabilityAsync("canpulseguide", cancellationToken);
        CanSetRightAscensionRate = await TryGetCapabilityAsync("cansetrightascensionrate", cancellationToken);
        CanSetDeclinationRate = await TryGetCapabilityAsync("cansetdeclinationrate", cancellationToken);
        CanSetGuideRates = await TryGetCapabilityAsync("cansetguiderates", cancellationToken);

        _equatorialSystem = (EquatorialCoordinateType)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "equatorialsystem", cancellationToken);

        // Cache CanMoveAxis for all 3 axes
        for (int axis = 0; axis < 3; axis++)
        {
            _canMoveAxis[axis] = await TryGetCapabilityAsync($"canmoveaxis?Axis={axis}", cancellationToken);
        }

        // Cache current rate values
        try { _rightAscensionRate = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "rightascensionrate", cancellationToken); } catch { }
        try { _declinationRate = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "declinationrate", cancellationToken); } catch { }
        try { _guideRateRA = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "guideraterightascension", cancellationToken); } catch { }
        try { _guideRateDec = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "guideratedeclination", cancellationToken); } catch { }

        // TODO: query tracking rates from Alpaca when the endpoint supports enumeration

        return true;
    }

    private async Task<bool> TryGetCapabilityAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public bool CanSetTracking { get; private set; }

    public bool CanSetSideOfPier { get; private set; }

    public bool CanPark { get; private set; }

    public bool CanUnpark { get; private set; }

    public bool CanSetPark { get; private set; }

    public bool CanSlew { get; private set; }

    public bool CanSlewAsync { get; private set; }

    public bool CanSync { get; private set; }

    public bool CanPulseGuide { get; private set; }

    public bool CanSetRightAscensionRate { get; private set; }

    public bool CanSetDeclinationRate { get; private set; }

    public bool CanSetGuideRates { get; private set; }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => _trackingSpeeds;

    // Cached static properties
    private EquatorialCoordinateType _equatorialSystem;
    private bool[] _canMoveAxis = [false, false, false];

    // Write-through cached properties
    private double _rightAscensionRate, _declinationRate, _guideRateRA, _guideRateDec;

    public EquatorialCoordinateType EquatorialSystem => _equatorialSystem;

    public bool TimeIsSetByUs { get; private set; }

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_rightAscensionRate);

    public async ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
    {
        await Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "rightascensionrate", [new("RightAscensionRate", value.ToString(CultureInfo.InvariantCulture))]);
        _rightAscensionRate = value;
    }

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_declinationRate);

    public async ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
    {
        await Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "declinationrate", [new("DeclinationRate", value.ToString(CultureInfo.InvariantCulture))]);
        _declinationRate = value;
    }

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_guideRateRA);

    public async ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        await Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "guideraterightascension", [new("GuideRateRightAscension", value.ToString(CultureInfo.InvariantCulture))]);
        _guideRateRA = value;
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_guideRateDec);

    public async ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
    {
        await Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "guideratedeclination", [new("GuideRateDeclination", value.ToString(CultureInfo.InvariantCulture))]);
        _guideRateDec = value;
    }

    public async ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
    {
        var rate = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "trackingrate", cancellationToken);
        return (TrackingSpeed)rate;
    }

    public async ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("trackingrate", [new("TrackingRate", ((int)value).ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken)
    {
        return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "athome", cancellationToken);
    }

    public async ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
    {
        return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "atpark", cancellationToken);
    }

    public async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "slewing", cancellationToken);
    }

    public async ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "siderealtime", cancellationToken);
    }

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return ConditionHA(lst - ra);
    }

    public async ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dateStr = await Client.GetStringAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "utcdate", cancellationToken);
            return dateStr is not null && DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask SetUTCDateAsync(DateTime value, CancellationToken cancellationToken)
    {
        try
        {
            await PutMethodAsync("utcdate", [new("UTCDate", value.ToString("o", CultureInfo.InvariantCulture))], cancellationToken);
            TimeIsSetByUs = true;
        }
        catch
        {
            TimeIsSetByUs = false;
        }
    }

    public async ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
    {
        return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "tracking", cancellationToken);
    }

    public async ValueTask SetTrackingAsync(bool value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("tracking", [new("Tracking", value.ToString())], cancellationToken);
    }

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        var val = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "sideofpier", cancellationToken);
        return (PointingState)val;
    }

    public async ValueTask SetSideOfPierAsync(PointingState value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("sideofpier", [new("SideOfPier", ((int)value).ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        // Alpaca destinationsideofpier is a GET with query parameters for RA and Dec
        var val = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber,
            $"destinationsideofpier?RightAscension={ra.ToString(CultureInfo.InvariantCulture)}&Declination={dec.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        return (PointingState)val;
    }

    public async ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
    {
        var val = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "alignmentmode", cancellationToken);
        return (AlignmentMode)val;
    }

    public async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "rightascension", cancellationToken);
    }

    public async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "declination", cancellationToken);
    }

    public async ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "siteelevation", cancellationToken);
    }

    public async ValueTask SetSiteElevationAsync(double value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("siteelevation", [new("SiteElevation", value.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "sitelatitude", cancellationToken);
    }

    public async ValueTask SetSiteLatitudeAsync(double value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("sitelatitude", [new("SiteLatitude", value.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "sitelongitude", cancellationToken);
    }

    public async ValueTask SetSiteLongitudeAsync(double value, CancellationToken cancellationToken)
    {
        await PutMethodAsync("sitelongitude", [new("SiteLongitude", value.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
    {
        return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "ispulseguiding", cancellationToken);
    }

    public async ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        await PutMethodAsync("park", cancellationToken: cancellationToken);
    }

    public async ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        await PutMethodAsync("unpark", cancellationToken: cancellationToken);
    }

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        await PutMethodAsync("pulseguide",
        [
            new("Direction", ((int)direction).ToString(CultureInfo.InvariantCulture)),
            new("Duration", ((int)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
        ], cancellationToken);
    }

    public async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        await PutMethodAsync("slewtocoordinatesasync",
        [
            new("RightAscension", ra.ToString(CultureInfo.InvariantCulture)),
            new("Declination", dec.ToString(CultureInfo.InvariantCulture))
        ], cancellationToken);
    }

    public async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        await PutMethodAsync("synctocoordinates",
        [
            new("RightAscension", ra.ToString(CultureInfo.InvariantCulture)),
            new("Declination", dec.ToString(CultureInfo.InvariantCulture))
        ], cancellationToken);
    }

    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        await PutMethodAsync("abortslew", cancellationToken: cancellationToken);
    }

    public bool CanMoveAxis(TelescopeAxis axis) => (int)axis >= 0 && (int)axis < _canMoveAxis.Length && _canMoveAxis[(int)axis];

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis)
    {
        // TODO: parse axis rates from Alpaca response
        throw new NotImplementedException();
    }

    public async ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        await PutMethodAsync("moveaxis",
        [
            new("Axis", ((int)axis).ToString(CultureInfo.InvariantCulture)),
            new("Rate", rate.ToString(CultureInfo.InvariantCulture))
        ], cancellationToken);
    }

    public async ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "targetrightascension", cancellationToken);
    }

    public async ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
    {
        return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "targetdeclination", cancellationToken);
    }
}
