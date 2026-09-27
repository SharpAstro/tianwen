using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// A client's edit of a profile, written through its node's one profile writer (P3 of docs/plans/hardware-in-the-server.md,
    /// #930), for every client that edits one: the GUI's and the TUI's Equipment tab, and the CLI's <c>profile</c> verbs (P6,
    /// #936). An edit names the revision it was read at; one the node refuses as made against a profile that has moved on (a
    /// 412: another client, or the node's own write after a connect or a discovery) is made again onto the profile as it is
    /// now (<see cref="ProfileDataExtensions.RebasedOnto"/>), so neither change is lost.
    /// </summary>
    public static class NodeProfileWrites
    {
        /// <summary>How many times an edit is made again onto a profile that keeps moving on before it is given up.</summary>
        public const int Attempts = 3;

        /// <summary>
        /// Writes <paramref name="edited"/>, an edit of <paramref name="madeOf"/> as it was read at <paramref name="revision"/>,
        /// and answers the profile as the node stored it, or the node's refusal. Nothing but the node decides what is stored.
        /// </summary>
        /// <param name="name">A new name, or null to keep the profile's.</param>
        public static async Task<NodeResult<ProfileDetailDto>> WriteAsync(TianWenNodeClient client, Guid profileId, ProfileData madeOf,
            ProfileData edited, string revision, string? name, ILogger logger, CancellationToken cancellationToken)
        {
            var data = edited;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var written = await client.UpdateProfileAsync(profileId, data, revision, name, cancellationToken).ConfigureAwait(false);
                if (written.IsSuccess || written.StatusCode != 412)
                {
                    return written;
                }

                // Moved on since the edit was read: made again onto the profile as the node holds it now.
                var latest = await client.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
                if (latest is not { IsSuccess: true, Value: { Data: { } now, Revision: { } nowRevision } })
                {
                    return new NodeResult<ProfileDetailDto>(null, $"it could not be read again to make the change: {latest.Error}", latest.StatusCode);
                }
                logger.LogInformation("Profile {ProfileId} moved on since the edit was read; making it again onto revision {Revision}",
                    profileId, nowRevision);
                data = data.RebasedOnto(madeOf, now);
                madeOf = now;
                revision = nowRevision;
            }
            return new NodeResult<ProfileDetailDto>(null, "it kept changing while the change was made", 412);
        }
    }
}
