using DIR.Lib;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the prompt overlay's physical-presence caution (docs/plans/remote-profile.md P4).
    /// <para>
    /// <c>RequiresPhysicalPresence</c> has crossed the wire since P2 but nothing rendered it, which left
    /// a remote operator looking at a bare green "Continue" for a question only somebody standing at the
    /// telescope can honestly answer. Clicking it asserts a physical fact the session cannot verify --
    /// the same fabrication the unattended-answer policy exists to prevent, just performed by a human.
    /// </para>
    /// <para>
    /// The point is not that answering is forbidden (the operator may be on the phone with someone at
    /// the scope, or the panel is on a smart plug): it is that the UI must stop presenting it as a
    /// neutral one-click default.
    /// </para>
    /// </summary>
    [Collection("UI")]
    public class PhysicalPresencePromptTests
    {
        private static SessionPromptEventArgs Prompt(bool requiresPhysicalPresence) =>
            new SessionPromptEventArgs(
                "Manual flat panel",
                "Switch on the flat panel for OTA 1, then Continue.",
                "Continue",
                "Cancel",
                new TaskCompletionSource<bool>(),
                requiresPhysicalPresence);

        private static LiveSessionTab<RgbaImage> Tab(string? remoteRigName)
        {
            var renderer = new RgbaImageRenderer(1280, 800);
            return new LiveSessionTab<RgbaImage>(renderer)
            {
                DpiScale = 1f,
                FontPath = FontResolver.ResolveSystemFont(),
                RemoteRigName = remoteRigName,
            };
        }

        [Fact]
        public void AnOrdinaryPromptGetsNoCaution()
        {
            // Only prompts gating a PHYSICAL act warn. Warning on every prompt would train the operator
            // to dismiss the warning, which is worse than not having one.
            Tab(remoteRigName: null).PhysicalPresenceWarning(Prompt(requiresPhysicalPresence: false))
                .ShouldBeNull();
        }

        [Fact]
        public void ALocalPresencePromptStillWarns()
        {
            // "Physical presence" means somebody at the telescope -- and the person at the keyboard may
            // be indoors two rooms away, so this is not a remote-only hazard.
            var warning = Tab(remoteRigName: null).PhysicalPresenceWarning(Prompt(requiresPhysicalPresence: true));

            warning.ShouldNotBeNull();
            warning.ShouldContain("at the telescope");
            warning.ShouldContain("cannot check", Case.Sensitive,
                "the operator has to understand the session is trusting them, not verifying");
        }

        [Fact]
        public void AremotePresencePromptNamesTheRig()
        {
            // Watching a rig makes it certain the operator is not there, so the wording says where.
            var warning = Tab(remoteRigName: "Observatory").PhysicalPresenceWarning(Prompt(requiresPhysicalPresence: true));

            warning.ShouldNotBeNull();
            warning.ShouldContain("Observatory");
        }

        [Fact]
        public void TheWarningIsTheOnlyDifferenceRemoteMakes()
        {
            // Both contexts warn; only the location wording differs. A remote-only warning would leave
            // the local case silently unguarded.
            var local = Tab(remoteRigName: null).PhysicalPresenceWarning(Prompt(true));
            var remote = Tab(remoteRigName: "Observatory").PhysicalPresenceWarning(Prompt(true));

            local.ShouldNotBeNull();
            remote.ShouldNotBeNull();
            local.ShouldNotBe(remote);
        }

        [Fact]
        public void AnEmptyRigNameFallsBackToTheGenericWording()
        {
            // A context whose display name has not resolved yet must not render "Someone has to be at ".
            var warning = Tab(remoteRigName: "").PhysicalPresenceWarning(Prompt(true));

            warning.ShouldNotBeNull();
            warning.ShouldContain("at the telescope");
        }
    }
}
