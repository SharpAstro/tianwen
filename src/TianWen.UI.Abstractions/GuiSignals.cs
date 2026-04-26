using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>Request device discovery.</summary>
public readonly record struct DiscoverDevicesSignal(bool IncludeFake = false);

/// <summary>Add a new OTA to the active profile.</summary>
public readonly record struct AddOtaSignal;

/// <summary>Start editing site coordinates.</summary>
public readonly record struct EditSiteSignal;

/// <summary>Start creating a new profile.</summary>
public readonly record struct CreateProfileSignal;

/// <summary>Assign a discovered device to the active slot.</summary>
public readonly record struct AssignDeviceSignal(int DeviceIndex);

/// <summary>Connect a device via the device hub (out-of-session).</summary>
public readonly record struct ConnectDeviceSignal(System.Uri DeviceUri);

/// <summary>
/// Connect every assigned device in the active profile that is currently
/// resolvable by the device hub or appears in the discovery cache. The handler
/// fans out to per-URI <see cref="ConnectDeviceSignal"/> dispatch — bulk action
/// composed from the existing single-device flow rather than a parallel one,
/// so PendingTransitions / notifications stay consistent.
/// </summary>
public readonly record struct ConnectAllDevicesSignal;

/// <summary>Disconnect a device via the device hub (out-of-session).
/// The handler safety-checks first; for an unsafe device it sets a confirmation
/// state instead of disconnecting immediately.</summary>
public readonly record struct DisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Warm a cooled camera to ambient (gradual ramp), then disconnect.
/// Posted from the per-row "Warm &amp; Disconnect" confirmation button.</summary>
public readonly record struct WarmAndDisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Force-disconnect bypassing safety checks (no warm-up, no idle wait).
/// Posted only after the secondary force-confirmation button is clicked.</summary>
public readonly record struct ForceDisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Set the cooler setpoint (°C) on a hub-connected camera and turn the cooler on.</summary>
public readonly record struct SetCoolerSetpointSignal(System.Uri DeviceUri, double SetpointC);

/// <summary>Direct cooler-off (bypass safety). Posted only after force confirmation,
/// or when the camera is already safe (idle + near ambient).</summary>
public readonly record struct SetCoolerOffSignal(System.Uri DeviceUri);

/// <summary>Warm a cooled camera to ambient (gradual ramp), then turn the cooler off
/// without disconnecting. Posted from the cooler-panel "Warm up &amp; Off" confirmation.</summary>
public readonly record struct WarmAndCoolerOffSignal(System.Uri DeviceUri);

/// <summary>Update profile data (filter config, OTA props, etc.).</summary>
public readonly record struct UpdateProfileSignal(ProfileData Data);

/// <summary>Build the observation schedule from pinned targets.</summary>
public readonly record struct BuildScheduleSignal;

/// <summary>Toggle fullscreen mode.</summary>
public readonly record struct ToggleFullscreenSignal;

/// <summary>Request plate solving the current image.</summary>
public readonly record struct PlateSolveSignal;

/// <summary>Planner session state changed (proposals, sliders, settings). Triggers auto-save.</summary>
public readonly record struct SavePlannerSessionSignal;

/// <summary>Session configuration changed. Triggers auto-save.</summary>
public readonly record struct SaveSessionConfigSignal;

/// <summary>Start a new session from the current profile, config, and schedule.</summary>
public readonly record struct StartSessionSignal;

/// <summary>
/// Begin the SharpCap-style polar-alignment routine on the manually-connected
/// mount. Switches the live view into <see cref="LiveSessionMode.PolarAlign"/>.
/// </summary>
/// <param name="OtaIndex">Which OTA's camera to use as the capture source when
/// <paramref name="UseGuider"/> is false. Defaults to the auto-pick from
/// <c>CaptureSourceRanker</c> when -1. Ignored when <paramref name="UseGuider"/>
/// is true.</param>
/// <param name="DeltaRaDeg">RA-axis rotation angle in degrees (typically 60 or 90).</param>
/// <param name="UseGuider">If true, use the connected guider (built-in or PHD2)
/// as the capture source instead of the main camera. PHD2 requires <c>Save Images</c>
/// to be enabled in its profile — failure to find a saved frame surfaces as a
/// failure reason on Phase A. Selected by the user via a separate UI toggle so
/// the choice is explicit; the auto-ranker remains a future enhancement.</param>
public readonly record struct StartPolarAlignmentSignal(
    int OtaIndex = -1,
    double DeltaRaDeg = 60.0,
    bool UseGuider = false);

/// <summary>
/// Cancel an in-flight polar-alignment routine. The orchestrator's reverse-axis
/// restore (per <c>PolarAlignmentOnDone</c>) still runs on disposal.
/// </summary>
public readonly record struct CancelPolarAlignmentSignal;

/// <summary>
/// User clicked Done after the routine reached <see cref="PolarAlignmentPhase.Aligned"/>.
/// Triggers the configured on-done behaviour (reverse-axis / park / leave-in-place).
/// </summary>
public readonly record struct DonePolarAlignmentSignal;

/// <summary>Request abort — shows confirmation strip in the live session tab.</summary>
public readonly record struct RequestAbortSessionSignal;

/// <summary>Confirmed abort — cancels the running session.</summary>
public readonly record struct ConfirmAbortSessionSignal;

/// <summary>
/// Take a single preview exposure on one OTA's camera and display it in the mini viewer.
/// NOT written to disk — transient. Only valid when no session is running.
/// </summary>
public readonly record struct TakePreviewSignal(
    int OtaIndex,
    double ExposureSeconds,
    int? Gain = null,
    short Binning = 1);

/// <summary>
/// Write the current preview frame to disk under a "Snapshot" target.
/// Only valid when a preview image exists and no session is running.
/// </summary>
public readonly record struct SaveSnapshotSignal(int OtaIndex = 0);

/// <summary>
/// Plate-solve the current preview image (in-memory, not FITS viewer).
/// Result stored in <see cref="LiveSessionState.PreviewPlateSolveResult"/>.
/// </summary>
public readonly record struct PlateSolvePreviewSignal(int OtaIndex = 0);

/// <summary>
/// Jog the focuser by a relative step amount. Positive = outward, negative = inward.
/// Only valid when no session is running and the focuser is connected.
/// </summary>
public readonly record struct JogFocuserSignal(int OtaIndex, int Steps);

/// <summary>
/// Open the F3 sky-map search modal. Posted from the sky-map tab's key handler;
/// subscriber wires up callbacks and lazy-builds the catalog index.
/// </summary>
public readonly record struct OpenSkyMapSearchSignal;

/// <summary>
/// Close the F3 sky-map search modal (Esc, click-outside, or close button).
/// </summary>
public readonly record struct CloseSkyMapSearchSignal;

/// <summary>
/// The search modal text changed — recompute the result list.
/// Posted from the search input's <c>OnTextChanged</c> callback so filtering
/// runs on the render thread alongside other signal processing.
/// </summary>
public readonly record struct SkyMapSearchQueryChangedSignal;

/// <summary>
/// Commit the currently-highlighted search result: slew the sky map, populate
/// the info panel, close the modal.
/// </summary>
public readonly record struct SkyMapSearchCommitSignal;

/// <summary>
/// User clicked on the sky map itself (non-drag). Payload is the pixel
/// coordinates; the handler projects to RA/Dec and picks the nearest object.
/// </summary>
public readonly record struct SkyMapClickSelectSignal(float ScreenX, float ScreenY);

/// <summary>
/// Toggle an object in <see cref="PlannerState.Proposals"/> from the sky map
/// info panel. Unpins if already pinned, otherwise pins. Handler triggers
/// the usual schedule recompute and persistence.
/// </summary>
public readonly record struct SkyMapPinObjectSignal(
    string Name,
    double RA,
    double Dec,
    TianWen.Lib.Astrometry.Catalogs.CatalogIndex? Index,
    TianWen.Lib.Astrometry.Catalogs.ObjectType ObjectType);

/// <summary>
/// Switch to the Planner tab and scroll the target matching this object into view.
/// If the object isn't already in <c>TonightsBest</c> / <c>SearchResults</c>, it is
/// added as a search result so the planner can display it.
/// </summary>
public readonly record struct ViewInPlannerSignal(
    string Name,
    double RA,
    double Dec,
    TianWen.Lib.Astrometry.Catalogs.CatalogIndex? Index,
    TianWen.Lib.Astrometry.Catalogs.ObjectType ObjectType);

/// <summary>
/// Goto: command the active profile's connected mount to slew to this object's
/// J2000 coordinates. Handler picks Solar/Lunar tracking from <c>Index</c> with
/// fallback to Sidereal, enforces <c>PlannerState.MinHeightAboveHorizon</c> (min 1),
/// and refuses when a session is running or the mount is not connected.
/// A two-click confirmation is required for <c>CatalogIndex.Sol</c>.
/// </summary>
public readonly record struct SkyMapSlewToObjectSignal(
    string Name,
    double RA,
    double Dec,
    TianWen.Lib.Astrometry.Catalogs.CatalogIndex? Index,
    TianWen.Lib.Astrometry.Catalogs.ObjectType ObjectType);

/// <summary>
/// Open the sky-map info panel for a non-catalog fixed point (NCP / SCP / Zenith).
/// Handler builds <see cref="SkyMapInfoPanelData"/> via
/// <see cref="SkyMapInfoPanelData.FromPosition"/> and assigns it to
/// <c>SkyMapSearchState.InfoPanel</c> so the standard panel renders with a Goto
/// button. Slewing always goes through that button — clicking the marker on the
/// map only opens the panel, never slews directly.
/// </summary>
public readonly record struct SkyMapShowFixedPointInfoSignal(
    string Name,
    double RaHours,
    double DecDeg);
