using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using static TianWen.Hosting.Api.Alpaca.AlpacaHandlers;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>
    /// The Alpaca member tables: one lookup per device type, mapping the URL's member name to a handler
    /// over this node's own driver.
    /// <para>
    /// <b>Scope is deliberately the members our own <c>AlpacaClient</c> calls</b>, which is a known,
    /// enumerable set and yields a free round-trip test (our client against our server). Full ASCOM
    /// conformance -- so N.I.N.A. or SharpCap could drive a rig -- is a much larger bar and a separate
    /// feature; anything outside this table answers <see cref="AlpacaError.NotImplemented"/>, which is a
    /// legitimate ASCOM response rather than a lie.
    /// </para>
    /// <para>
    /// Member names are lower-case because Alpaca URLs are, and lookups are case-insensitive because
    /// clients are inconsistent about it.
    /// </para>
    /// </summary>
    public static class AlpacaMembers
    {
        /// <summary>
        /// Members every ASCOM device has. <c>connected</c> is here and is the one settable member that is
        /// NOT actuation -- see <see cref="AlpacaMember.IsActuation"/>.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, AlpacaMember>> Common() =>
        [
            // Read only HERE. The `connected` PUT is handled directly by the endpoint, because connecting
            // has to go through the hub (which owns driver instances and lifetimes) rather than through a
            // driver this table would first have to obtain -- and obtaining one is the very thing the
            // client is asking to do. Disconnecting likewise has to consult the ownership gate.
            new("connected", AlpacaMember.Get(Sync<IDeviceDriver>(d => AlpacaValue.Of(d.Connected)))),

            new("name", AlpacaMember.Get(Sync<IDeviceDriver>(d => AlpacaValue.Of(d.Name)))),
            new("description", AlpacaMember.Get(Sync<IDeviceDriver>(d => AlpacaValue.Of(d.Description)))),
            new("driverinfo", AlpacaMember.Get(Sync<IDeviceDriver>(d => AlpacaValue.Of(d.DriverInfo)))),
            new("driverversion", AlpacaMember.Get(Sync<IDeviceDriver>(d => AlpacaValue.Of(d.DriverVersion?.ToString())))),

            // 3 = ASCOM Platform 6 device interface. We implement a subset of it (see the class doc), and
            // reporting 3 is what makes our own client take the Platform-6 code paths it was written for.
            new("interfaceversion", AlpacaMember.Get(Sync<IDeviceDriver>(_ => AlpacaValue.Of(3)))),
        ];

        private static FrozenDictionary<string, AlpacaMember> Build(
            IEnumerable<KeyValuePair<string, AlpacaMember>> specific) =>
            Common().Concat(specific).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>The table for one Alpaca device type, or null when the type is not served.</summary>
        public static FrozenDictionary<string, AlpacaMember>? For(string alpacaType) => alpacaType.ToLowerInvariant() switch
        {
            "telescope" => Telescope,
            "focuser" => Focuser,
            "filterwheel" => FilterWheel,
            "covercalibrator" => CoverCalibrator,
            "camera" => Camera,
            _ => null,
        };

        // -----------------------------------------------------------------------------------------
        // Telescope (a TianWen mount)
        // -----------------------------------------------------------------------------------------

        private static readonly FrozenDictionary<string, AlpacaMember> Telescope = Build(
        [
            new("alignmentmode", AlpacaMember.Get(async (d, ct) => AlpacaValue.Of((int)await As<IMountDriver>(d).GetAlignmentAsync(ct)))),
            new("athome", AlpacaMember.Get(Bool<IMountDriver>((m, ct) => m.AtHomeAsync(ct)))),
            new("atpark", AlpacaMember.Get(Bool<IMountDriver>((m, ct) => m.AtParkAsync(ct)))),
            new("slewing", AlpacaMember.Get(Bool<IMountDriver>((m, ct) => m.IsSlewingAsync(ct)))),
            new("ispulseguiding", AlpacaMember.Get(Bool<IMountDriver>((m, ct) => m.IsPulseGuidingAsync(ct)))),

            new("canpark", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanPark)))),
            new("cansetpark", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetPark)))),
            new("canunpark", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanUnpark)))),
            new("canslew", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSlew)))),
            new("canslewasync", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSlewAsync)))),
            new("cansync", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSync)))),
            new("canpulseguide", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanPulseGuide)))),
            new("cansettracking", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetTracking)))),
            new("cansetpierside", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetSideOfPier)))),
            new("cansetguiderates", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetGuideRates)))),
            new("cansetrightascensionrate", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetRightAscensionRate)))),
            new("cansetdeclinationrate", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of(m.CanSetDeclinationRate)))),

            new("rightascension", AlpacaMember.Get(Double<IMountDriver>((m, ct) => m.GetRightAscensionAsync(ct)))),
            new("declination", AlpacaMember.Get(Double<IMountDriver>((m, ct) => m.GetDeclinationAsync(ct)))),
            new("siderealtime", AlpacaMember.Get(Double<IMountDriver>((m, ct) => m.GetSiderealTimeAsync(ct)))),
            new("targetrightascension", AlpacaMember.Get(Double<IMountDriver>((m, ct) => m.GetTargetRightAscensionAsync(ct)))),
            new("targetdeclination", AlpacaMember.Get(Double<IMountDriver>((m, ct) => m.GetTargetDeclinationAsync(ct)))),
            new("sideofpier", AlpacaMember.Get(async (d, ct) => AlpacaValue.Of((int)await As<IMountDriver>(d).GetSideOfPierAsync(ct)))),
            new("equatorialsystem", AlpacaMember.Get(Sync<IMountDriver>(m => AlpacaValue.Of((int)m.EquatorialSystem)))),

            new("tracking", AlpacaMember.GetSet(
                Bool<IMountDriver>((m, ct) => m.IsTrackingAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetTrackingAsync(p.Bool("Tracking"), ct)))),
            new("trackingrate", AlpacaMember.GetSet(
                async (d, ct) => AlpacaValue.Of((int)await As<IMountDriver>(d).GetTrackingSpeedAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetTrackingSpeedAsync((TrackingSpeed)p.Int("TrackingRate"), ct)))),

            new("rightascensionrate", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetRightAscensionRateAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetRightAscensionRateAsync(p.Double("RightAscensionRate"), ct)))),
            new("declinationrate", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetDeclinationRateAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetDeclinationRateAsync(p.Double("DeclinationRate"), ct)))),
            new("guideraterightascension", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetGuideRateRightAscensionAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetGuideRateRightAscensionAsync(p.Double("GuideRateRightAscension"), ct)))),
            new("guideratedeclination", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetGuideRateDeclinationAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetGuideRateDeclinationAsync(p.Double("GuideRateDeclination"), ct)))),

            new("siteelevation", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetSiteElevationAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetSiteElevationAsync(p.Double("SiteElevation"), ct)))),
            new("sitelatitude", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetSiteLatitudeAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetSiteLatitudeAsync(p.Double("SiteLatitude"), ct)))),
            new("sitelongitude", AlpacaMember.GetSet(
                Double<IMountDriver>((m, ct) => m.GetSiteLongitudeAsync(ct)),
                Do<IMountDriver>((m, p, ct) => m.SetSiteLongitudeAsync(p.Double("SiteLongitude"), ct)))),

            new("utcdate", AlpacaMember.GetSet(
                async (d, ct) => AlpacaValue.Of((await As<IMountDriver>(d).TryGetUTCDateFromMountAsync(ct))
                    ?.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") ?? ""),
                Do<IMountDriver>((m, p, ct) => throw new AlpacaFault(AlpacaError.NotImplemented,
                    "Setting the mount clock over Alpaca is not served; the node owns its own time source")))),

            new("park", AlpacaMember.Action(Do<IMountDriver>((m, _, ct) => m.ParkAsync(ct)))),
            new("unpark", AlpacaMember.Action(Do<IMountDriver>((m, _, ct) => m.UnparkAsync(ct)))),
            new("abortslew", AlpacaMember.Action(Do<IMountDriver>((m, _, ct) => m.AbortSlewAsync(ct)))),
            new("slewtocoordinatesasync", AlpacaMember.Action(Do<IMountDriver>((m, p, ct) =>
                m.BeginSlewRaDecAsync(p.Double("RightAscension"), p.Double("Declination"), ct)))),
            new("synctocoordinates", AlpacaMember.Action(Do<IMountDriver>((m, p, ct) =>
                m.SyncRaDecAsync(p.Double("RightAscension"), p.Double("Declination"), ct)))),
            new("pulseguide", AlpacaMember.Action(Do<IMountDriver>((m, p, ct) =>
                m.StartPulseGuideAsync((GuideDirection)p.Int("Direction"), TimeSpan.FromMilliseconds(p.Int("Duration")), ct)))),
            new("moveaxis", AlpacaMember.Action(Do<IMountDriver>((m, p, ct) =>
                m.MoveAxisAsync((TelescopeAxis)p.Int("Axis"), p.Double("Rate"), ct)))),
        ]);

        // -----------------------------------------------------------------------------------------
        // Focuser
        // -----------------------------------------------------------------------------------------

        private static readonly FrozenDictionary<string, AlpacaMember> Focuser = Build(
        [
            new("position", AlpacaMember.Get(Int<IFocuserDriver>((f, ct) => f.GetPositionAsync(ct)))),
            new("ismoving", AlpacaMember.Get(Bool<IFocuserDriver>((f, ct) => f.GetIsMovingAsync(ct)))),
            new("temperature", AlpacaMember.Get(Double<IFocuserDriver>((f, ct) => f.GetTemperatureAsync(ct)))),
            new("absolute", AlpacaMember.Get(Sync<IFocuserDriver>(f => AlpacaValue.Of(f.Absolute)))),
            new("maxincrement", AlpacaMember.Get(Sync<IFocuserDriver>(f => AlpacaValue.Of(f.MaxIncrement)))),
            new("maxstep", AlpacaMember.Get(Sync<IFocuserDriver>(f => AlpacaValue.Of(f.MaxStep)))),
            new("tempcompavailable", AlpacaMember.Get(Sync<IFocuserDriver>(f => AlpacaValue.Of(f.TempCompAvailable)))),

            // ASCOM says StepSize throws when the driver cannot report it; NotImplemented is that, and is
            // what our own client already handles.
            new("stepsize", AlpacaMember.Get(Sync<IFocuserDriver>(f => f.CanGetStepSize
                ? AlpacaValue.Of(f.StepSize)
                : throw new AlpacaFault(AlpacaError.NotImplemented, "This focuser does not report its step size")))),

            new("tempcomp", AlpacaMember.GetSet(
                Bool<IFocuserDriver>((f, ct) => f.GetTempCompAsync(ct)),
                Do<IFocuserDriver>((f, p, ct) => f.SetTempCompAsync(p.Bool("TempComp"), ct)))),

            new("move", AlpacaMember.Action(DoTask<IFocuserDriver>((f, p, ct) => f.BeginMoveAsync(p.Int("Position"), ct)))),
            new("halt", AlpacaMember.Action(DoTask<IFocuserDriver>((f, _, ct) => f.BeginHaltAsync(ct)))),
        ]);

        // -----------------------------------------------------------------------------------------
        // Filter wheel
        // -----------------------------------------------------------------------------------------

        private static readonly FrozenDictionary<string, AlpacaMember> FilterWheel = Build(
        [
            new("position", AlpacaMember.GetSet(
                Int<IFilterWheelDriver>((w, ct) => w.GetPositionAsync(ct)),
                DoTask<IFilterWheelDriver>((w, p, ct) => w.BeginMoveAsync(p.Int("Position"), ct)))),

            new("names", AlpacaMember.Get(Sync<IFilterWheelDriver>(w =>
                AlpacaValue.Of(w.Filters.Select(static f => f.DisplayName).ToArray())))),

            // Focus offsets are a profile fact on this node, not a driver fact, so a zero per filter is
            // the honest answer rather than a fabricated number: TianWen applies its own offsets
            // internally and an Alpaca client applying them again would double-correct.
            new("focusoffsets", AlpacaMember.Get(Sync<IFilterWheelDriver>(w =>
                AlpacaValue.Of(new int[Math.Max(1, w.Filters.Count)])))),
        ]);

        // -----------------------------------------------------------------------------------------
        // Cover / calibrator
        // -----------------------------------------------------------------------------------------

        private static readonly FrozenDictionary<string, AlpacaMember> CoverCalibrator = Build(
        [
            new("coverstate", AlpacaMember.Get(async (d, ct) => AlpacaValue.Of((int)await As<ICoverDriver>(d).GetCoverStateAsync(ct)))),
            new("calibratorstate", AlpacaMember.Get(async (d, ct) => AlpacaValue.Of((int)await As<ICoverDriver>(d).GetCalibratorStateAsync(ct)))),
            new("brightness", AlpacaMember.Get(Int<ICoverDriver>((c, ct) => c.GetBrightnessAsync(ct)))),
            new("maxbrightness", AlpacaMember.Get(Sync<ICoverDriver>(c => AlpacaValue.Of(c.MaxBrightness)))),

            new("opencover", AlpacaMember.Action(DoTask<ICoverDriver>((c, _, ct) => c.BeginOpen(ct)))),
            new("closecover", AlpacaMember.Action(DoTask<ICoverDriver>((c, _, ct) => c.BeginClose(ct)))),
            new("calibratoroff", AlpacaMember.Action(DoTask<ICoverDriver>((c, _, ct) => c.BeginCalibratorOff(ct)))),
            new("calibratoron", AlpacaMember.Action(DoTask<ICoverDriver>((c, p, ct) => c.BeginCalibratorOn(p.Int("Brightness"), ct)))),
        ]);

        // -----------------------------------------------------------------------------------------
        // Camera
        //
        // imagearray is NOT here: it is served as binary ImageBytes by the endpoint, which sidesteps
        // JSON entirely. Encoding a full frame as decimal-ASCII integers -- what the legacy JSON
        // imagearray does -- is an order of magnitude slower, and our own client negotiates ImageBytes.
        // -----------------------------------------------------------------------------------------

        private static readonly FrozenDictionary<string, AlpacaMember> Camera = Build(
        [
            new("camerastate", AlpacaMember.Get(async (d, ct) => AlpacaValue.Of((int)await As<ICameraDriver>(d).GetCameraStateAsync(ct)))),
            new("imageready", AlpacaMember.Get(Bool<ICameraDriver>((c, ct) => c.GetImageReadyAsync(ct)))),
            new("cameraxsize", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CameraXSize)))),
            new("cameraysize", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CameraYSize)))),
            new("pixelsizex", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.PixelSizeX)))),
            new("pixelsizey", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.PixelSizeY)))),
            new("maxadu", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.MaxADU)))),
            new("maxbinx", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of((int)c.MaxBinX)))),
            new("maxbiny", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of((int)c.MaxBinY)))),
            new("sensortype", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of((int)c.SensorType)))),
            new("bayeroffsetx", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.BayerOffsetX)))),
            new("bayeroffsety", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.BayerOffsetY)))),
            new("electronsperadu", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.ElectronsPerADU)))),
            new("fullwellcapacity", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.FullWellCapacity)))),
            new("exposureresolution", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.ExposureResolution)))),
            new("lastexposureduration", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.LastExposureDuration?.TotalSeconds ?? 0d)))),

            new("canabortexposure", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanAbortExposure)))),
            new("canstopexposure", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanStopExposure)))),
            new("canfastreadout", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanFastReadout)))),
            new("cangetcoolerpower", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanGetCoolerPower)))),
            new("cansetccdtemperature", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanSetCCDTemperature)))),
            new("canpulseguide", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.CanPulseGuide)))),
            new("ispulseguiding", AlpacaMember.Get(Bool<ICameraDriver>((c, ct) => c.GetIsPulseGuidingAsync(ct)))),

            new("ccdtemperature", AlpacaMember.Get(Double<ICameraDriver>((c, ct) => c.GetCCDTemperatureAsync(ct)))),
            new("heatsinktemperature", AlpacaMember.Get(Double<ICameraDriver>((c, ct) => c.GetHeatSinkTemperatureAsync(ct)))),
            new("coolerpower", AlpacaMember.Get(Double<ICameraDriver>((c, ct) => c.GetCoolerPowerAsync(ct)))),

            new("gainmin", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of((int)c.GainMin)))),
            new("gainmax", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of((int)c.GainMax)))),
            new("gains", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.Gains?.ToArray() ?? [])))),
            new("offsetmin", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.OffsetMin)))),
            new("offsetmax", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.OffsetMax)))),
            new("offsets", AlpacaMember.Get(Sync<ICameraDriver>(c => AlpacaValue.Of(c.Offsets?.ToArray() ?? [])))),
            // ICameraDriver models readout mode as a NAME, not an indexed list, so there is no honest
            // list to publish. NotImplemented is the ASCOM answer, and our own client already handles it.
            new("readoutmodes", AlpacaMember.Get(Sync<ICameraDriver>(_ =>
                throw new AlpacaFault(AlpacaError.NotImplemented, "This camera does not enumerate readout modes")))),

            new("cooleron", AlpacaMember.GetSet(
                Bool<ICameraDriver>((c, ct) => c.GetCoolerOnAsync(ct)),
                Do<ICameraDriver>((c, p, ct) => c.SetCoolerOnAsync(p.Bool("CoolerOn"), ct)))),
            new("setccdtemperature", AlpacaMember.GetSet(
                Double<ICameraDriver>((c, ct) => c.GetSetCCDTemperatureAsync(ct)),
                Do<ICameraDriver>((c, p, ct) => c.SetSetCCDTemperatureAsync(p.Double("SetCCDTemperature"), ct)))),

            new("binx", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.BinX)),
                Do<ICameraDriver>((c, p, ct) => { c.BinX = p.Int("BinX"); return ValueTask.CompletedTask; }))),
            new("biny", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.BinY)),
                Do<ICameraDriver>((c, p, ct) => { c.BinY = p.Int("BinY"); return ValueTask.CompletedTask; }))),
            new("startx", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.StartX)),
                Do<ICameraDriver>((c, p, ct) => { c.StartX = p.Int("StartX"); return ValueTask.CompletedTask; }))),
            new("starty", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.StartY)),
                Do<ICameraDriver>((c, p, ct) => { c.StartY = p.Int("StartY"); return ValueTask.CompletedTask; }))),
            new("numx", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.NumX)),
                Do<ICameraDriver>((c, p, ct) => { c.NumX = p.Int("NumX"); return ValueTask.CompletedTask; }))),
            new("numy", AlpacaMember.GetSet(
                Sync<ICameraDriver>(c => AlpacaValue.Of(c.NumY)),
                Do<ICameraDriver>((c, p, ct) => { c.NumY = p.Int("NumY"); return ValueTask.CompletedTask; }))),
            new("gain", AlpacaMember.GetSet(
                async (d, ct) => AlpacaValue.Of((int)await As<ICameraDriver>(d).GetGainAsync(ct)),
                Do<ICameraDriver>((c, p, ct) => c.SetGainAsync((short)p.Int("Gain"), ct)))),
            new("offset", AlpacaMember.GetSet(
                Int<ICameraDriver>((c, ct) => c.GetOffsetAsync(ct)),
                Do<ICameraDriver>((c, p, ct) => c.SetOffsetAsync(p.Int("Offset"), ct)))),

            // ASCOM's readoutmode is an INDEX into readoutmodes; TianWen models it as a name and does not
            // enumerate them, so neither half can be answered honestly. Both say NotImplemented rather
            // than inventing an index that would select the wrong mode.
            new("readoutmode", AlpacaMember.Get(Sync<ICameraDriver>(_ =>
                throw new AlpacaFault(AlpacaError.NotImplemented, "This camera does not index its readout modes")))),

            new("fastreadout", AlpacaMember.GetSet(
                Bool<ICameraDriver>((c, ct) => c.GetFastReadoutAsync(ct)),
                Do<ICameraDriver>((c, p, ct) => c.SetFastReadoutAsync(p.Bool("FastReadout"), ct)))),

            // ASCOM's Light flag maps onto TianWen's richer FrameType: a light frame or, when the shutter
            // stays closed, a dark. Bias/flat are TianWen concepts the Alpaca caller cannot express, and
            // guessing one from a zero-length exposure would mislabel the FITS.
            new("startexposure", AlpacaMember.Action(Do<ICameraDriver>(async (c, p, ct) =>
                _ = await c.StartExposureAsync(
                    TimeSpan.FromSeconds(p.Double("Duration")),
                    p.Bool("Light") ? FrameType.Light : FrameType.Dark,
                    ct)))),
            new("abortexposure", AlpacaMember.Action(Do<ICameraDriver>((c, _, ct) => c.AbortExposureAsync(ct)))),
            new("stopexposure", AlpacaMember.Action(Do<ICameraDriver>((c, _, ct) => c.StopExposureAsync(ct)))),
            new("pulseguide", AlpacaMember.Action(Do<ICameraDriver>((c, p, ct) =>
                c.StartPulseGuideAsync((GuideDirection)p.Int("Direction"), TimeSpan.FromMilliseconds(p.Int("Duration")), ct)))),
        ]);
    }
}
