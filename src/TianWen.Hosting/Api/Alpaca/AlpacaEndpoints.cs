using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices.Alpaca;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>
    /// Serves this node's devices over the ASCOM Alpaca REST API -- the <b>device plane</b> of
    /// docs/plans/remote-profile.md P5.
    /// <para>
    /// <b>Why Alpaca instead of a bespoke hub API.</b> The client side then needs no new code at all:
    /// <c>AddAlpaca()</c> is already a working, simulator-tested device source for exactly the types a
    /// rig exposes, so a remote <c>IDeviceHub</c> gets real drivers for free, and the binary ImageBytes
    /// transfer comes with it.
    /// </para>
    /// <para>
    /// <b>It is a device plane, not a session plane, and cannot become one.</b> Alpaca has no vocabulary
    /// for session lifecycle, schedule, phase, prompts, notifications, autofocus, flats or meridian
    /// flips -- and ASCOM has no Guider device type at all. Native v1 remains the session plane by
    /// necessity, not by preference.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> A rig's session holds borrowed drivers, so an Alpaca client issuing
    /// <c>startexposure</c> on the same camera would create two masters. Alpaca cannot express "a
    /// session owns this", but it can return an error number, so the facade asks the same
    /// <see cref="DeviceOwnershipGate"/> every other surface asks and answers
    /// <see cref="AlpacaError.InvalidOperation"/> with the gate's own wording. Note what is NOT gated:
    /// reads always work (watching a rig must cost it nothing), and <c>connected</c> keeps working
    /// because every standard Alpaca client PUTs it before reading anything -- refusing it would make a
    /// running rig unreadable exactly when someone most wants to look.
    /// </para>
    /// <para>
    /// <b>URL coexistence.</b> Alpaca's <c>/api/v1/{deviceType}/{deviceNumber}/{member}</c> shares a
    /// prefix with native v1, but the two never collide: native uses <c>session</c>, <c>devices</c>,
    /// <c>profiles</c>, <c>preview</c> and <c>image</c>, none of which is an ASCOM device type. The
    /// prefix is fixed by the Alpaca spec, so this is not a choice.
    /// </para>
    /// </summary>
    public static class AlpacaEndpoints
    {
        /// <summary>Server-side transaction counter, monotonic for the process lifetime as the spec requires.</summary>
        private static int _serverTransactionId;

        /// <summary>The ASCOM device types this facade serves.</summary>
        private const string DeviceTypeConstraint = "camera|telescope|focuser|filterwheel|covercalibrator";

        public static IEndpointRouteBuilder MapAlpacaApi(this IEndpointRouteBuilder routes)
        {
            MapManagement(routes);

            // imagearray is served as binary ImageBytes, so it bypasses the generic dispatcher entirely.
            // Mapped BEFORE the catch-all member route so it wins the match.
            routes.MapGet("/api/v1/camera/{deviceNumber:int}/imagearray",
                async (int deviceNumber, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
                    await ImageArrayAsync(deviceNumber, http, sp, ct));

            routes.MapGet($"/api/v1/{{deviceType:regex(^{DeviceTypeConstraint}$)}}/{{deviceNumber:int}}/{{member}}",
                (string deviceType, int deviceNumber, string member, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
                    HandleAsync(deviceType, deviceNumber, member, http, sp, isWrite: false, ct));

            routes.MapPut($"/api/v1/{{deviceType:regex(^{DeviceTypeConstraint}$)}}/{{deviceNumber:int}}/{{member}}",
                (string deviceType, int deviceNumber, string member, HttpContext http, IServiceProvider sp, CancellationToken ct) =>
                    HandleAsync(deviceType, deviceNumber, member, http, sp, isWrite: true, ct));

            return routes;
        }

        private static void MapManagement(IEndpointRouteBuilder routes)
        {
            routes.MapGet("/management/apiversions", (HttpContext http) =>
                Results.Json(
                    Envelope(new[] { 1 }, http),
                    AlpacaServerJsonContext.Default.AlpacaResponseInt32Array));

            routes.MapGet("/management/v1/description", (HttpContext http) =>
                Results.Json(
                    Envelope("TianWen node", http),
                    AlpacaServerJsonContext.Default.AlpacaResponseString));

            routes.MapGet("/management/v1/configureddevices", (HttpContext http, IServiceProvider sp) =>
            {
                var catalog = BuildCatalog(sp);
                var devices = catalog.Entries.Select(static e => new AlpacaConfiguredDevice
                {
                    DeviceName = e.DisplayName,
                    DeviceType = char.ToUpperInvariant(e.AlpacaType[0]) + e.AlpacaType[1..],
                    DeviceNumber = e.Number,
                    UniqueID = AlpacaDeviceCatalog.UniqueId(e.DeviceUri),
                }).ToArray();

                return Results.Json(
                    Envelope(devices, http),
                    AlpacaServerJsonContext.Default.AlpacaResponseAlpacaConfiguredDeviceArray);
            });
        }

        /// <summary>
        /// The active profile's devices. Rebuilt per request rather than cached: a profile switch must be
        /// visible on the next <c>configureddevices</c> read, and the build is a walk over a handful of
        /// URIs.
        /// </summary>
        private static AlpacaDeviceCatalog BuildCatalog(IServiceProvider sp)
        {
            var hosted = sp.GetRequiredService<IHostedSession>();
            var discovery = sp.GetRequiredService<IDeviceDiscovery>();

            var profile = discovery.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .FirstOrDefault(p => p.ProfileId == hosted.ActiveProfileId);

            return AlpacaDeviceCatalog.FromProfile(profile?.Data);
        }

        private static async Task<IResult> HandleAsync(
            string deviceType, int deviceNumber, string member,
            HttpContext http, IServiceProvider sp, bool isWrite, CancellationToken cancellationToken)
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AlpacaEndpoints));

            if (AlpacaMembers.For(deviceType) is not { } table)
            {
                return Fault(AlpacaError.NotImplemented, $"Device type '{deviceType}' is not served", http);
            }

            if (!BuildCatalog(sp).TryResolve(deviceType, deviceNumber, out var entry))
            {
                return Fault(AlpacaError.InvalidValue, $"No {deviceType} with device number {deviceNumber}", http);
            }

            if (!table.TryGetValue(member, out var spec))
            {
                return Fault(AlpacaError.NotImplemented, $"'{member}' is not implemented on {deviceType}", http);
            }

            var hub = sp.GetRequiredService<IDeviceHub>();
            var isConnectedMember = string.Equals(member, "connected", StringComparison.OrdinalIgnoreCase);

            // Ownership, before anything touches hardware. Reads pass through unconditionally, and so
            // does `connected` -- see the class doc for why the gate is per-member and not "all PUTs".
            if (isWrite && spec.IsActuation)
            {
                var verdict = DeviceOwnershipGate.Evaluate(hub, entry.DeviceUri, DeviceAction.Actuate);
                if (!verdict.Allowed)
                {
                    return Fault(AlpacaError.InvalidOperation, verdict.Describe(), http);
                }
            }

            try
            {
                if (isWrite && isConnectedMember)
                {
                    return await ConnectOrDisconnectAsync(hub, entry, http, cancellationToken).ConfigureAwait(false);
                }

                if (isWrite)
                {
                    if (spec.Write is not { } write)
                    {
                        return Fault(AlpacaError.NotImplemented, $"'{member}' is read-only", http);
                    }

                    var form = http.Request.HasFormContentType
                        ? await http.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
                        : null;

                    await write(Connected(hub, entry), new AlpacaParameters(form), cancellationToken).ConfigureAwait(false);
                    return Results.Json(MethodEnvelope(http), AlpacaServerJsonContext.Default.AlpacaMethodResponse);
                }

                if (spec.Read is not { } read)
                {
                    return Fault(AlpacaError.NotImplemented, $"'{member}' is write-only", http);
                }

                // A `connected` READ on a device the hub never connected is false, not an error -- that is
                // the question the client is asking.
                if (isConnectedMember && !hub.TryGetConnectedDriver<IDeviceDriver>(entry.DeviceUri, out _))
                {
                    return Results.Json(Envelope(false, http), AlpacaServerJsonContext.Default.AlpacaResponseBoolean);
                }

                var value = await read(Connected(hub, entry), cancellationToken).ConfigureAwait(false);
                return Payload(value, http);
            }
            catch (AlpacaFault fault)
            {
                return Fault(fault.ErrorNumber, fault.Message, http);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A driver fault must not become a bodiless 500: an Alpaca client parses the envelope, and
                // an HTTP-level error tells it nothing about what went wrong.
                logger.LogWarning(ex, "Alpaca {Method} {DeviceType}/{Number}/{Member} failed",
                    isWrite ? "PUT" : "GET", deviceType, deviceNumber, member);
                return Fault(AlpacaError.UnspecifiedError, ex.Message, http);
            }
        }

        /// <summary>
        /// <c>GET camera/{n}/imagearray</c> as binary ImageBytes.
        /// <para>
        /// Only ever <i>reads</i> the driver's current frame. It must not release or recycle it: the
        /// buffer belongs to the camera, and handing it back to the pool here would corrupt the next
        /// local read of the same frame.
        /// </para>
        /// </summary>
        private static async Task<IResult> ImageArrayAsync(
            int deviceNumber, HttpContext http, IServiceProvider sp, CancellationToken cancellationToken)
        {
            if (!BuildCatalog(sp).TryResolve("camera", deviceNumber, out var entry))
            {
                return ImageBytesFault(AlpacaError.InvalidValue, $"No camera with device number {deviceNumber}");
            }

            var hub = sp.GetRequiredService<IDeviceHub>();
            if (!hub.TryGetConnectedDriver<ICameraDriver>(entry.DeviceUri, out var camera))
            {
                return ImageBytesFault(AlpacaError.NotConnected, $"{entry.DisplayName} is not connected");
            }

            if (!await camera.GetImageReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                // ASCOM's answer for "you asked before the exposure finished".
                return ImageBytesFault(AlpacaError.InvalidOperation, "No image is ready");
            }

            var image = await camera.GetImageAsync(cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return ImageBytesFault(AlpacaError.UnspecifiedError, "The camera reported a ready image but returned none");
            }

            var payload = AlpacaImageBytesWriter.Encode(image);
            http.Response.Headers["Content-Type"] = AlpacaImageBytesMimeType;
            return Results.Bytes(payload, AlpacaImageBytesMimeType);
        }

        /// <summary>The negotiated binary transfer type. Mirrors <c>AlpacaImageBytes.MimeType</c>, which
        /// is internal to TianWen.Lib.</summary>
        private const string AlpacaImageBytesMimeType = "application/imagebytes";

        private static IResult ImageBytesFault(int errorNumber, string message) =>
            Results.Bytes(AlpacaImageBytesWriter.EncodeError(errorNumber, message), AlpacaImageBytesMimeType);

        /// <summary>The hub's connected driver, or a NotConnected fault -- the ASCOM answer for using a
        /// device before connecting it.</summary>
        private static IDeviceDriver Connected(IDeviceHub hub, AlpacaDeviceEntry entry) =>
            hub.TryGetConnectedDriver<IDeviceDriver>(entry.DeviceUri, out var driver)
                ? driver
                : throw new AlpacaFault(AlpacaError.NotConnected, $"{entry.DisplayName} is not connected");

        /// <summary>
        /// The <c>connected</c> PUT, routed through the hub rather than through a driver.
        /// <para>
        /// <b>Connect</b> has to create the driver, which is the hub's job -- and going through the hub is
        /// also what makes the device visible to every other surface (the Equipment tab, the session), so
        /// a rig connected remotely is not connected twice locally.
        /// </para>
        /// <para>
        /// <b>Disconnect is ownership-checked</b>, which is the invariant the plan called out by name: a
        /// remote <c>Connected = false</c> must never take a driver away from a running session. The hub
        /// would refuse anyway (that is the P0 lease), but asking the gate first turns an exception into
        /// the same explanatory sentence every other surface shows.
        /// </para>
        /// </summary>
        private static async Task<IResult> ConnectOrDisconnectAsync(
            IDeviceHub hub, AlpacaDeviceEntry entry, HttpContext http, CancellationToken cancellationToken)
        {
            var form = http.Request.HasFormContentType
                ? await http.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
                : null;

            var wanted = new AlpacaParameters(form).Bool("Connected");

            if (wanted)
            {
                if (!hub.TryGetDeviceFromUri(entry.DeviceUri, out var device))
                {
                    return Fault(AlpacaError.InvalidValue, $"Cannot resolve {entry.DeviceUri}", http);
                }

                await hub.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
                return Results.Json(MethodEnvelope(http), AlpacaServerJsonContext.Default.AlpacaMethodResponse);
            }

            var verdict = DeviceOwnershipGate.Evaluate(hub, entry.DeviceUri, DeviceAction.Disconnect);
            if (!verdict.Allowed)
            {
                return Fault(AlpacaError.InvalidOperation, verdict.Describe(), http);
            }

            await hub.DisconnectAsync(entry.DeviceUri, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Results.Json(MethodEnvelope(http), AlpacaServerJsonContext.Default.AlpacaMethodResponse);
        }

        // -----------------------------------------------------------------------------------------
        // Envelopes
        // -----------------------------------------------------------------------------------------

        private static int NextTransactionId() => Interlocked.Increment(ref _serverTransactionId);

        private static int ClientTransactionId(HttpContext http) =>
            int.TryParse(http.Request.Query["ClientTransactionID"], out var id) ? id : 0;

        private static AlpacaResponse<T> Envelope<T>(T value, HttpContext http) => new AlpacaResponse<T>
        {
            Value = value,
            ClientTransactionID = ClientTransactionId(http),
            ServerTransactionID = NextTransactionId(),
            ErrorNumber = AlpacaError.Ok,
            ErrorMessage = "",
        };

        private static AlpacaMethodResponse MethodEnvelope(HttpContext http) => new AlpacaMethodResponse
        {
            ClientTransactionID = ClientTransactionId(http),
            ServerTransactionID = NextTransactionId(),
            ErrorNumber = AlpacaError.Ok,
            ErrorMessage = "",
        };

        /// <summary>
        /// An Alpaca failure: <b>HTTP 200</b> with a non-zero ErrorNumber. The spec reserves HTTP error
        /// statuses for malformed requests; a device that refuses is a successful exchange reporting a
        /// device-level error, and a client that saw a 4xx would treat the node as broken.
        /// </summary>
        private static IResult Fault(int errorNumber, string message, HttpContext http) =>
            Results.Json(
                new AlpacaMethodResponse
                {
                    ClientTransactionID = ClientTransactionId(http),
                    ServerTransactionID = NextTransactionId(),
                    ErrorNumber = errorNumber,
                    ErrorMessage = message,
                },
                AlpacaServerJsonContext.Default.AlpacaMethodResponse);

        /// <summary>
        /// Serializes a read through the source-generated context for its concrete type. The switch is
        /// what keeps this AOT-safe: an <c>object</c> payload has no resolvable metadata under AOT.
        /// </summary>
        private static IResult Payload(AlpacaValue value, HttpContext http) => value.Kind switch
        {
            AlpacaValueKind.Bool => Results.Json(Envelope(value.Bool, http), AlpacaServerJsonContext.Default.AlpacaResponseBoolean),
            AlpacaValueKind.Int => Results.Json(Envelope(value.Int, http), AlpacaServerJsonContext.Default.AlpacaResponseInt32),
            AlpacaValueKind.Double => Results.Json(Envelope(JsonNumber.ForWire(value.Double), http), AlpacaServerJsonContext.Default.AlpacaResponseDouble),
            AlpacaValueKind.String => Results.Json(Envelope(value.String ?? "", http), AlpacaServerJsonContext.Default.AlpacaResponseString),
            AlpacaValueKind.StringArray => Results.Json(Envelope(value.StringArray ?? [], http), AlpacaServerJsonContext.Default.AlpacaResponseStringArray),
            AlpacaValueKind.IntArray => Results.Json(Envelope(value.IntArray ?? [], http), AlpacaServerJsonContext.Default.AlpacaResponseInt32Array),
            _ => Results.Json(MethodEnvelope(http), AlpacaServerJsonContext.Default.AlpacaMethodResponse),
        };
    }
}
