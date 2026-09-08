using System;
using System.Collections.Generic;
using System.Text;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Builds the two links the viewer's "?" panel offers: the user guide, and a PREPARED GitHub issue
    /// with the environment already filled in.
    /// </summary>
    /// <remarks>
    /// <para><b>Prepared rather than filed.</b> One URL and one shell open, so there is no token, no API
    /// client, no network code and nothing that can rot. It also leaves the user reading what they are
    /// about to send, which a silent POST does not, and it works identically for someone who is not
    /// signed in: GitHub asks them to sign in and keeps the draft.</para>
    /// <para><b>Nothing here names a place the user has been.</b> The detail a maintainer wants is in the
    /// log, and a log lists every folder opened; folder names carry target names and site names, and a
    /// run of them says where someone was and when they were not at home. So the body says WHERE the
    /// logs are and asks for one, and it never embeds an absolute path (which would carry the account
    /// name), never the open document's path, and never a line of the log itself. Attaching is the
    /// user's own act, on a file they can read first. That is the whole reason this is not
    /// "auto-create an issue, with attaching logs" as originally asked for.</para>
    /// <para>The environment block is the half a user cannot be expected to produce accurately and the
    /// half every report needs: which build, which OS, and whether the AI stack resolved.</para>
    /// </remarks>
    public static class BugReportLink
    {
        /// <summary>The viewer's user guide (P13), on the org site rather than restated in the panel.</summary>
        public const string DocumentationUrl = "https://sharpastro.github.io/guide/viewer.html";

        private const string NewIssueUrl = "https://github.com/SharpAstro/tianwen/issues/new";

        /// <summary>
        /// A cap on the whole URL, well inside every browser's limit and GitHub's own.
        /// </summary>
        /// <remarks>
        /// The AI block is the only unbounded part (one line per probed directory, and a directory can
        /// be long), so it is what gets trimmed. Trimming beats risking a link that a browser silently
        /// truncates: a body cut off mid-word is obvious to the user, a dropped query parameter is not.
        /// </remarks>
        internal const int MaxUrlLength = 6000;

        /// <summary>
        /// The prefilled issue URL. <paramref name="aiLines"/> is the "?" panel's own AI block, so the
        /// report says exactly what the panel said.
        /// </summary>
        public static string ForViewer(string build, string osDescription, IReadOnlyList<string>? aiLines)
        {
            var body = new StringBuilder();
            body.Append("### What happened\n\n");
            body.Append("_Replace this with what you did and what the viewer did._\n\n");
            body.Append("### Environment\n\n");
            body.Append("- Build: ").Append(Clean(build)).Append('\n');
            body.Append("- OS: ").Append(Clean(osDescription)).Append('\n');

            if (aiLines is { Count: > 0 })
            {
                body.Append("- AI enhancement:\n");
                foreach (var line in aiLines)
                {
                    body.Append("  - ").Append(Clean(line)).Append('\n');
                }
            }
            else
            {
                body.Append("- AI enhancement: not probed\n");
            }

            // Stated in the issue rather than only in the panel, because the person who reads the issue
            // is the one who needs to know a log exists and was deliberately not attached.
            body.Append('\n');
            body.Append("### Log\n\n");
            body.Append(@"Not attached. Logs are under `%LOCALAPPDATA%\TianWen\Logs`, newest folder ");
            body.Append("first, one `FitsViewer_*.log` per run. Drag one in if you are happy to share ");
            body.Append("it: it lists the folders opened during that run.\n");

            var url = Compose("Viewer: ", body.ToString());
            if (url.Length <= MaxUrlLength)
            {
                return url;
            }

            // Over budget: drop the AI block, which is the only part that grows with the machine, and
            // say so rather than leaving a maintainer wondering why it is absent.
            var trimmed = new StringBuilder();
            trimmed.Append("### What happened\n\n");
            trimmed.Append("_Replace this with what you did and what the viewer did._\n\n");
            trimmed.Append("### Environment\n\n");
            trimmed.Append("- Build: ").Append(Clean(build)).Append('\n');
            trimmed.Append("- OS: ").Append(Clean(osDescription)).Append('\n');
            trimmed.Append("- AI enhancement: omitted, too long for a link. It is on the viewer's `?` panel.\n");
            return Compose("Viewer: ", trimmed.ToString());
        }

        private static string Compose(string title, string body)
            => NewIssueUrl + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);

        /// <summary>
        /// Flattens a value into one line. A newline inside a bullet would break the list, and both
        /// inputs come from outside this class (a version string, an OS description, a probe result).
        /// </summary>
        private static string Clean(string value)
            => string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
