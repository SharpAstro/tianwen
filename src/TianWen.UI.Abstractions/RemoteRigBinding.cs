using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// A local record pointing at a profile that lives on a rig's <c>tianwen-server</c>.
    /// <para>
    /// <b>Not a synced profile</b>, and that distinction is the whole design. A profile's device URIs
    /// name drivers and ports on the machine the hardware is plugged into, so copying one here would
    /// produce a profile whose devices cannot exist locally. What is stored is the <i>binding</i>: which
    /// node, which profile on it, what to call it, and where it was last seen.
    /// </para>
    /// <para>
    /// Identity is <see cref="NodeId"/> -- LAN.Lib's stable per-node id -- never the address and never
    /// the display name. A rig moves between DHCP leases and gets renamed; neither should orphan its
    /// binding. <see cref="LastAddress"/> is a <i>hint</i> for showing "offline, last seen at ..." and
    /// for an optimistic first connect before discovery has caught up.
    /// </para>
    /// </summary>
    public sealed record RemoteRigBinding
    {
        /// <summary>Local id for this binding, and the file name it persists under.</summary>
        public required Guid BindingId { get; init; }

        /// <summary>LAN.Lib stable node id of the rig. The identity this binding is keyed on.</summary>
        public required string NodeId { get; init; }

        /// <summary>
        /// The profile on the rig to bind to, or null for "mirror whatever it runs".
        /// <para>
        /// Null is a first-class choice, not a missing value: a rig that plans and starts its own nights
        /// should be watched, not driven, and pinning a profile id would make the binding wrong the
        /// moment the rig switched profiles by itself.
        /// </para>
        /// </summary>
        public Guid? RemoteProfileId { get; init; }

        /// <summary>What to call this rig in the UI. Defaults to its announced service name.</summary>
        public required string Alias { get; init; }

        /// <summary>Where the rig was last reachable, e.g. <c>http://192.168.1.50:1888/</c>. A hint only.</summary>
        public string? LastAddress { get; init; }

        /// <summary>
        /// When the rig last actually answered, or null if it never has. UTC.
        /// <para>
        /// <b>Only meaningful across a restart.</b> While a rig is connected the live
        /// <c>RemoteSessionMirror.LastContactUtc</c> is the truth -- a rig that dies mid-watch keeps its
        /// connection, so the UI reads the real time from there and this field is not consulted. It
        /// exists so that after a restart an offline rig can still say "last seen 6 h ago" instead of
        /// only naming an address that may be years stale.
        /// </para>
        /// <para>
        /// Written twice per rig per run -- on first contact and on a clean quit -- rather than on every
        /// poll, which at the mirror's 500 ms active cadence would be a disk write twice a second per
        /// rig. A hard kill therefore leaves the first-contact stamp, which under-reports the age rather
        /// than inventing one.
        /// </para>
        /// <para>
        /// Distinct from an announcement: LAN.Lib's beacon tells you a rig is alive *now*, which is why
        /// there is no "last announced" here. This is the last time this app got an answer from it.
        /// </para>
        /// </summary>
        public DateTimeOffset? LastSeenUtc { get; init; }
    }

    /// <summary>
    /// Reads and writes <see cref="RemoteRigBinding"/> records under
    /// <c>{AppData}/RemoteProfiles/{bindingId}.json</c>, one file per binding.
    /// <para>
    /// One file per record rather than a single index, matching <c>Profiles/</c>: a corrupt or
    /// half-written file costs one rig, not every rig. Writes go through
    /// <see cref="IExternal.AtomicWriteJsonAsync"/>, so a crash mid-save cannot truncate a good record.
    /// </para>
    /// </summary>
    public static class RemoteRigPersistence
    {
        /// <summary>Folder name under the app data root.</summary>
        internal const string FolderName = "RemoteProfiles";

        private static string FolderPath(IExternal external) =>
            Path.Combine(external.ProfileFolder.FullName, FolderName);

        private static string FilePath(IExternal external, Guid bindingId) =>
            Path.Combine(FolderPath(external), $"{bindingId}.json");

        /// <summary>Writes one binding, creating the folder on first use.</summary>
        public static Task SaveAsync(RemoteRigBinding binding, IExternal external, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(FolderPath(external));
            return external.AtomicWriteJsonAsync(
                FilePath(external, binding.BindingId),
                binding,
                RemoteRigJsonContext.Default.RemoteRigBinding,
                cancellationToken);
        }

        /// <summary>
        /// Loads every binding, newest-named first is NOT implied -- order is by alias so the picker is
        /// stable between runs. A file that fails to parse is logged and skipped rather than failing the
        /// whole load: one bad record must not hide the others.
        /// </summary>
        public static async Task<ImmutableArray<RemoteRigBinding>> LoadAllAsync(
            IExternal external, ILogger? logger, CancellationToken cancellationToken)
        {
            var folder = FolderPath(external);
            if (!Directory.Exists(folder))
            {
                return [];
            }

            var loaded = new List<RemoteRigBinding>();
            foreach (var file in FileEnumeration.EnumerateFiles(folder, ".json", recursive: false))
            {
                var binding = await external
                    .TryReadJsonAsync(file, RemoteRigJsonContext.Default.RemoteRigBinding, logger, cancellationToken)
                    .ConfigureAwait(false);

                if (binding is not null)
                {
                    loaded.Add(binding);
                }
            }

            loaded.Sort(static (a, b) => string.Compare(a.Alias, b.Alias, StringComparison.OrdinalIgnoreCase));
            return [.. loaded];
        }

        /// <summary>Removes a binding. Missing file is not an error -- unbinding twice is harmless.</summary>
        public static void Delete(Guid bindingId, IExternal external, ILogger? logger)
        {
            var path = FilePath(external, bindingId);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not delete remote rig binding {BindingId}", bindingId);
            }
        }
    }

    [JsonSerializable(typeof(RemoteRigBinding))]
    internal partial class RemoteRigJsonContext : JsonSerializerContext
    {
    }
}
