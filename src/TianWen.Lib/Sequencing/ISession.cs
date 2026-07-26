using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A session that this process <b>owns and runs</b>: the observable state of
/// <see cref="ISessionTelemetry"/> plus the ability to act on it and the live driver graph behind it.
/// <para>
/// Consumers that only <i>read</i> should depend on <see cref="ISessionTelemetry"/> instead, so they
/// work unchanged against a session running on a remote node (docs/plans/remote-profile.md P3).
/// Everything here is what cannot cross a wire: the run methods (a session runs on the node that owns
/// the hardware -- never driven remotely per driver call) and <see cref="Setup"/>'s live drivers.
/// </para>
/// </summary>
public interface ISession : ISessionTelemetry, IAsyncDisposable
{
    /// <summary>
    /// The live device graph (mount + per-OTA camera/focuser/filter wheel/cover drivers) this session
    /// borrows for its run. Local-only by nature -- a UI that just needs to *display* equipment names
    /// should read <see cref="ISessionTelemetry.TelescopeCameraNames"/> /
    /// <see cref="ISessionTelemetry.MountDisplayName"/>, which a remote mirror can also supply.
    /// </summary>
    Setup Setup { get; }

    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// On-demand flat-frame run outside the normal <see cref="RunAsync"/> workflow: connects only the devices
    /// flats need (cameras / covers / filter wheels / focusers, plus the mount for sky-flats -- never the
    /// guider), cools to the imaging setpoint, captures flats per <see cref="SessionConfiguration.FlatSource"/>
    /// (<paramref name="skyFlatPeriod"/> selects dawn vs dusk for <see cref="FlatIlluminationSource.TwilightSky"/>),
    /// then finalises (abort exposures, close covers, warm cameras, park + disconnect). Skips wait-for-dark,
    /// focus, guider calibration and the observation loop entirely. Backs the CLI <c>tianwen flats</c> command
    /// and the <c>POST /api/v1/session/flats</c> endpoint.
    /// </summary>
    Task RunFlatsOnlyAsync(TwilightPeriod skyFlatPeriod, CancellationToken cancellationToken);
}
