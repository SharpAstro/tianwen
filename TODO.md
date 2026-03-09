# TODOs

## Sequencing / Session

- [ ] Gracefully stop a session (`HostedSession.cs:39`)
- [ ] Wait until 5 min to astro dark, and/or implement `IExternal.IsPolarAligned` (`Session.cs:61`)
- [ ] Maybe slew slightly above/below 0 declination to avoid trees, etc. (`Session.cs:235`)
- [ ] Plate solve, sync, and re-slew after initial slew (`Session.cs:245`)
- [ ] Wait until target rises again instead of skipping (`Session.cs:455`)
- [ ] Plate solve and re-slew during observation (`Session.cs:467`)
- [ ] Per-camera exposure calculation, e.g. via f/ratio (`Session.cs:540`)
- [ ] Stop exposures before meridian flip (if we can, and if there are any) (`Session.cs:668`)
- [ ] Stop guiding, flip, resync, verify, and restart guiding (`Session.cs:672`)
- [ ] Make FITS output path configurable, add frame type (`Session.cs:893`)

## Camera / ICameraDriver

- [ ] Consider using external temp sensor if no heatsink temp is available (`ICameraDriver.cs:314`)

## DAL Camera Driver

- [ ] Implement trigger for ReadoutMode (`DALCameraDriver.cs:290`)
- [ ] Add proper exceptions for `SetCCDTemperature` setter (`DALCameraDriver.cs:381`)
- [ ] Add proper exceptions for `Offset` getter (`DALCameraDriver.cs:661`)
- [ ] Support auto-exposure (`DALCameraDriver.cs:848`)

## Alpaca Drivers

- [ ] Query tracking rates from Alpaca when endpoint supports enumeration (`AlpacaTelescopeDriver.cs:46`)
- [ ] Parse axis rates from Alpaca response (`AlpacaTelescopeDriver.cs:315`)
- [ ] Implement string[] and int[] typed getters for filter names and focus offsets (`AlpacaFilterWheelDriver.cs:30`)
- [ ] Parse string[] from Alpaca for `Offsets` (`AlpacaCameraDriver.cs:238`)
- [ ] Parse string[] from Alpaca for `Gains` (`AlpacaCameraDriver.cs:248`)
- [ ] Alpaca `imagearray` endpoint requires special binary handling (`AlpacaCameraDriver.cs:258`)
- [ ] Async call to `lastexposureduration` endpoint (`AlpacaCameraDriver.cs:262`)

## ASCOM Drivers

- [ ] Implement axis rates for telescope (`AscomTelescopeDriver.cs:320`)

## Mount / Meade LX200 Protocol

- [ ] Determine precision based on firmware/patchlevel (`MeadeLX200ProtocolMountDriverBase.cs:43`)
- [ ] LX800 fixed GW response not being terminated, account for that (`MeadeLX200ProtocolMountDriverBase.cs:143`)
- [ ] Pier side detection only works for GEM mounts (`MeadeLX200ProtocolMountDriverBase.cs:305`)
- [ ] Support `:RgSS.S#` to set guide rate on AutoStar II (`MeadeLX200ProtocolMountDriverBase.cs:573,583`)
- [ ] Verify `:Q#` stops pulse guiding as well (`MeadeLX200ProtocolMountDriverBase.cs:873`)
- [ ] Use standard atmosphere for `SitePressure` (`IMountDriver.cs:344`)
- [ ] Check online or via connected devices for `SiteTemperature` (`IMountDriver.cs:345`)
- [ ] Handle refraction — assumes driver does not support/do refraction (`IMountDriver.cs:347`)

## Device Management

- [ ] Try to parse URI manually in Profile fallback (`Profile.cs:130`)

## External / Infrastructure

- [ ] Free unmanaged resources and override finalizer in `External.Dispose` (`External.cs:85-91`)
- [ ] Actually ensure that FITS library writes async (`IExternal.cs:226`)

## Imaging

- [ ] Not sure if `SensorType` LRGB check is correct (`SensorType.cs:54`)

## Astrometry / Catalogs

- [x] Update lib to accept spans in `CatalogUtils` (`CatalogUtils.cs:326,360`)

## Testing

- [ ] VDB has objects listed as `Be*`, but in HIP we only know stars (`*`) (`CelestialObjectDBTests.cs:73`)
- [ ] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`)

## Statistics

- [ ] Find a faster way to multiply all values in an array/span (`StatisticsHelper.cs:167`)

## Guider

- [ ] `appState` parameter should probably be an enum (`GuiderStateChangedEventArgs.cs:34`)
