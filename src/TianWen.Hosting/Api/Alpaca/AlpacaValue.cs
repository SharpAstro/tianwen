using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>
    /// ASCOM error numbers this facade returns. The full range is 0x400-0xFFF; only the ones a client
    /// can act on differently are listed.
    /// </summary>
    public static class AlpacaError
    {
        /// <summary>No error.</summary>
        public const int Ok = 0;

        /// <summary>The device does not implement this member.</summary>
        public const int NotImplemented = 0x400;

        /// <summary>A parameter was missing or unparseable.</summary>
        public const int InvalidValue = 0x401;

        /// <summary>The device is not connected.</summary>
        public const int NotConnected = 0x407;

        /// <summary>
        /// The operation is not valid in the device's current state. What a run's ownership lease
        /// returns: ASCOM has no "someone else owns this" code, and inventing a driver-specific one
        /// (0x500+) would be meaningless to a third-party client, whereas InvalidOperation renders as an
        /// error with our message attached.
        /// </summary>
        public const int InvalidOperation = 0x40B;

        /// <summary>An unexpected driver fault.</summary>
        public const int UnspecifiedError = 0x4FF;
    }

    /// <summary>Which JSON payload type an Alpaca read produced.</summary>
    public enum AlpacaValueKind
    {
        /// <summary>A PUT with no return value.</summary>
        None,
        Bool,
        Int,
        Double,
        String,
        StringArray,
        IntArray,
    }

    /// <summary>
    /// The result of reading one Alpaca member: a value plus which of the six payload types it is.
    /// <para>
    /// A tagged union rather than <c>object</c> deliberately -- the endpoint has to serialize through a
    /// source-generated <c>JsonTypeInfo</c> per concrete type, and an <c>object</c> payload cannot be
    /// resolved under Native AOT. This is the same rule that bans <c>ResponseEnvelope&lt;object&gt;</c>
    /// on the native v1 surface.
    /// </para>
    /// </summary>
    public readonly record struct AlpacaValue(
        AlpacaValueKind Kind,
        bool Bool = false,
        int Int = 0,
        double Double = 0,
        string? String = null,
        string[]? StringArray = null,
        int[]? IntArray = null)
    {
        public static AlpacaValue None => new AlpacaValue(AlpacaValueKind.None);
        public static AlpacaValue Of(bool value) => new AlpacaValue(AlpacaValueKind.Bool, Bool: value);
        public static AlpacaValue Of(int value) => new AlpacaValue(AlpacaValueKind.Int, Int: value);
        public static AlpacaValue Of(double value) => new AlpacaValue(AlpacaValueKind.Double, Double: value);
        public static AlpacaValue Of(string? value) => new AlpacaValue(AlpacaValueKind.String, String: value ?? "");
        public static AlpacaValue Of(string[] value) => new AlpacaValue(AlpacaValueKind.StringArray, StringArray: value);
        public static AlpacaValue Of(int[] value) => new AlpacaValue(AlpacaValueKind.IntArray, IntArray: value);
    }

    /// <summary>
    /// Refusal of one Alpaca call, carrying the ASCOM error number and the message a client shows.
    /// Thrown rather than returned because it can arise anywhere inside a handler; the endpoint turns it
    /// into a <b>200 OK with a non-zero ErrorNumber</b>, which is what the protocol specifies -- an HTTP
    /// error status is reserved for a malformed request, not for a device that said no.
    /// </summary>
    public sealed class AlpacaFault(int errorNumber, string message) : Exception(message)
    {
        public int ErrorNumber { get; } = errorNumber;
    }

    /// <summary>
    /// The parameters of an Alpaca PUT. Alpaca sends them form-encoded with <b>case-insensitive</b>
    /// names, which is easy to get wrong: a client sending <c>Connected=true</c> and a server reading
    /// <c>connected</c> would silently see nothing and treat it as false.
    /// </summary>
    public readonly struct AlpacaParameters(IFormCollection? form)
    {
        private readonly IFormCollection? _form = form;

        private string? Raw(string name)
        {
            if (_form is null)
            {
                return null;
            }

            foreach (var pair in _form)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value.ToString();
                }
            }

            return null;
        }

        /// <exception cref="AlpacaFault">Missing or unparseable.</exception>
        public bool Bool(string name) =>
            bool.TryParse(Raw(name), out var value)
                ? value
                : throw new AlpacaFault(AlpacaError.InvalidValue, $"'{name}' must be true or false");

        /// <exception cref="AlpacaFault">Missing or unparseable.</exception>
        public int Int(string name) =>
            int.TryParse(Raw(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new AlpacaFault(AlpacaError.InvalidValue, $"'{name}' must be an integer");

        /// <exception cref="AlpacaFault">Missing or unparseable.</exception>
        public double Double(string name) =>
            double.TryParse(Raw(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new AlpacaFault(AlpacaError.InvalidValue, $"'{name}' must be a number");
    }

    /// <summary>Reads one member off a connected driver.</summary>
    public delegate ValueTask<AlpacaValue> AlpacaRead(IDeviceDriver driver, CancellationToken cancellationToken);

    /// <summary>Writes a property or invokes a method on a connected driver.</summary>
    public delegate ValueTask AlpacaWrite(IDeviceDriver driver, AlpacaParameters parameters, CancellationToken cancellationToken);

    /// <summary>
    /// One Alpaca member: how to read it, how to write it, and whether writing it <b>commands the
    /// hardware</b>.
    /// </summary>
    /// <param name="Read">Null when the member is write-only (a method like <c>park</c>).</param>
    /// <param name="Write">Null when the member is read-only.</param>
    /// <param name="IsActuation">
    /// True when a write moves, exposes, or otherwise drives the device -- the writes refused while a run
    /// owns it. Deliberately per-member rather than "all PUTs": <c>connected</c> is a PUT and must keep
    /// working during a session, because a standard Alpaca client connects before reading anything, and
    /// refusing it would make a rig unreadable exactly when someone most wants to look at it.
    /// </param>
    public readonly record struct AlpacaMember(AlpacaRead? Read, AlpacaWrite? Write, bool IsActuation = false)
    {
        /// <summary>A read-only property.</summary>
        public static AlpacaMember Get(AlpacaRead read) => new AlpacaMember(read, null);

        /// <summary>A property that can be read and set, where setting drives the hardware.</summary>
        public static AlpacaMember GetSet(AlpacaRead read, AlpacaWrite write) => new AlpacaMember(read, write, IsActuation: true);

        /// <summary>A method (PUT only) that drives the hardware.</summary>
        public static AlpacaMember Action(AlpacaWrite write) => new AlpacaMember(null, write, IsActuation: true);

        /// <summary>A settable property that does NOT drive the hardware (currently only <c>connected</c>).</summary>
        public static AlpacaMember GetSetBenign(AlpacaRead read, AlpacaWrite write) => new AlpacaMember(read, write, IsActuation: false);
    }

    /// <summary>Convenience wrappers so the member tables read as one line each.</summary>
    public static class AlpacaHandlers
    {
        /// <summary>Casts to the driver interface, faulting with NotImplemented when the device is not of that kind.</summary>
        public static T As<T>(IDeviceDriver driver) where T : class, IDeviceDriver =>
            driver as T ?? throw new AlpacaFault(AlpacaError.NotImplemented,
                $"This device does not implement {typeof(T).Name}");

        public static AlpacaRead Bool<T>(Func<T, CancellationToken, ValueTask<bool>> read) where T : class, IDeviceDriver =>
            async (driver, ct) => AlpacaValue.Of(await read(As<T>(driver), ct).ConfigureAwait(false));

        public static AlpacaRead Int<T>(Func<T, CancellationToken, ValueTask<int>> read) where T : class, IDeviceDriver =>
            async (driver, ct) => AlpacaValue.Of(await read(As<T>(driver), ct).ConfigureAwait(false));

        public static AlpacaRead Double<T>(Func<T, CancellationToken, ValueTask<double>> read) where T : class, IDeviceDriver =>
            async (driver, ct) => AlpacaValue.Of(await read(As<T>(driver), ct).ConfigureAwait(false));

        /// <summary>A property with no I/O (a capability flag read off the driver object).</summary>
        public static AlpacaRead Sync<T>(Func<T, AlpacaValue> read) where T : class, IDeviceDriver =>
            (driver, _) => ValueTask.FromResult(read(As<T>(driver)));

        public static AlpacaWrite Do<T>(Func<T, AlpacaParameters, CancellationToken, ValueTask> write) where T : class, IDeviceDriver =>
            (driver, p, ct) => write(As<T>(driver), p, ct);

        /// <summary>Adapts a <see cref="Task"/>-returning driver method.</summary>
        public static AlpacaWrite DoTask<T>(Func<T, AlpacaParameters, CancellationToken, Task> write) where T : class, IDeviceDriver =>
            async (driver, p, ct) => await write(As<T>(driver), p, ct).ConfigureAwait(false);
    }
}
