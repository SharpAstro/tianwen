using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Web.Devices
{
    /// <summary>
    /// Browser/WASM implementation of <see cref="IExternal"/> for the in-browser planner + atlas.
    /// <para>
    /// The filesystem-shaped members (<see cref="AppDataFolder"/> et al. and the default
    /// atomic-JSON helpers on the interface) resolve against the emscripten in-memory filesystem
    /// (MEMFS) that .NET WASM provides: <see cref="Directory"/>/<see cref="File"/> work, they are
    /// simply not persistent across a page reload (fine for a showcase - planner pins survive a
    /// session; localStorage/IndexedDB persistence is a later refinement).
    /// </para>
    /// <para>
    /// Serial ports, the guider TCP socket, and FITS writing do not exist in the browser sandbox and
    /// are never reached by the planner or the atlas, so those members throw
    /// <see cref="PlatformNotSupportedException"/> rather than pretending to work.
    /// </para>
    /// </summary>
    public sealed class BrowserExternal(ICelestialObjectDB celestialObjectDB) : IExternal
    {
        private readonly ICelestialObjectDB _celestialObjectDB = celestialObjectDB;
        private DirectoryInfo? _appData;
        private DirectoryInfo? _images;
        private DirectoryInfo? _profiles;

        // All under the WASM MEMFS base dir; created on first access.
        public DirectoryInfo AppDataFolder => _appData ??= Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "appdata"));

        public DirectoryInfo ImageOutputFolder => _images ??= Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "appdata", "images"));

        public DirectoryInfo ProfileFolder => _profiles ??= Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "appdata", "profiles"));

        public async ValueTask<ICelestialObjectDB> GetCelestialObjectDBAsync(CancellationToken cancellationToken = default)
        {
            await _celestialObjectDB.InitDBAsync(cancellationToken: cancellationToken);
            return _celestialObjectDB;
        }

        public ValueTask WriteFitsFileAsync(Image image, string fileName)
            => throw new PlatformNotSupportedException("FITS writing is unavailable in the browser.");

        public ValueTask<ResourceLock> WaitForSerialPortEnumerationAsync(CancellationToken cancellationToken)
            => throw new PlatformNotSupportedException("Serial ports are unavailable in the browser.");

        public IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock resourceLock) => [];

        public ValueTask<ISerialConnection> OpenSerialDeviceAsync(string address, int baud, Encoding encoding,
            bool assertControlLines = false, CancellationToken cancellationToken = default)
            => throw new PlatformNotSupportedException("Serial ports are unavailable in the browser.");

        public Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address,
            CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default)
            => throw new PlatformNotSupportedException("Guider connections are unavailable in the browser.");
    }
}
