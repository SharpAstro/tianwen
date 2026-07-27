using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins device ownership: the hub-held lease that stops anything disconnecting or commanding a
    /// device while a run owns it.
    /// <para>
    /// <b>The bug this closes.</b> Ownership used to be guarded at five UI call sites, each testing
    /// <c>LiveSessionState.IsRunning</c> -- which is <i>false</i> during a flat run, and unset entirely
    /// by polar-align and planetary capture. So mid-flat-run the focuser could be jogged, the mount
    /// pulsed and slewed, and a planetary video capture started on the camera that was metering. The
    /// disconnect path was worse: <c>GetDisconnectSafetyAsync</c> returns <c>Safe</c> for anything that
    /// is not a camera, so the mount could be disconnected out from under a full session with no
    /// warning -- after which <c>ResilientCall</c> would silently reconnect it, undoing the operator's
    /// deliberate act, or fail five times and end the night.
    /// </para>
    /// <para>
    /// The fix is that ownership lives on <see cref="IDeviceHub"/>, which is the one thing the GUI, the
    /// TUI, the hosted API and (over P5) the Alpaca device plane all share. A UI flag could never have
    /// been the right predicate, because two of those four surfaces never see one.
    /// </para>
    /// </summary>
    public class DeviceOwnershipTests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        private static readonly Uri MountUri = new Uri("Mount://FakeDevice/FakeMount1");
        private static readonly Uri CameraUri = new Uri("Camera://FakeDevice/FakeCamera1");

        private static DeviceHub NewHub()
            => new DeviceHub(Substitute.For<IServiceProvider>(), NullLogger<DeviceHub>.Instance);

        // --- The claim itself -----------------------------------------------------------------------

        [Fact]
        public void AFreeDeviceCanBeClaimedAndOnlyOnce()
        {
            var hub = NewHub();

            hub.TryAcquireLease(MountUri, "the imaging session", out var first).ShouldBeTrue();
            first.ShouldNotBeNull();

            hub.TryAcquireLease(MountUri, "a second run", out var second).ShouldBeFalse();
            second.ShouldBeNull();

            hub.TryGetLease(MountUri, out var lease).ShouldBeTrue();
            lease.OwnerLabel.ShouldBe("the imaging session");
        }

        [Fact]
        public void ReleasingAClaimFreesTheDevice()
        {
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the imaging session", out var lease).ShouldBeTrue();

            lease.ShouldNotBeNull();
            lease.Dispose();

            hub.TryGetLease(MountUri, out _).ShouldBeFalse();
            hub.TryAcquireLease(MountUri, "the flat run", out _).ShouldBeTrue();
        }

        [Fact]
        public void AStaleHandleCannotReleaseTheCurrentOwnersClaim()
        {
            // The ABA case: a run ends, a second run claims the same device, and then something disposes
            // the first run's handle again (a double-dispose, or a finaliser racing a `using`). Releasing
            // on key alone would silently unlock the device the second run is actively driving.
            //
            // Deliberately the SAME owner label both times -- two sessions in one evening. That is the
            // dangerous case, because the two DeviceLease values are then equal, so a value-keyed removal
            // would happily drop the live claim. Different labels would let a value-keyed implementation
            // pass and prove nothing.
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the imaging session", out var firstHandle).ShouldBeTrue();
            firstHandle.ShouldNotBeNull();
            firstHandle.Dispose();

            hub.TryAcquireLease(MountUri, "the imaging session", out var secondHandle).ShouldBeTrue();
            secondHandle.ShouldNotBeNull();
            firstHandle.Dispose();

            hub.TryGetLease(MountUri, out var stillHeld).ShouldBeTrue("the second run must still own it");
            stillHeld.OwnerLabel.ShouldBe("the imaging session");

            // ... and the live handle still works.
            secondHandle.Dispose();
            hub.TryGetLease(MountUri, out _).ShouldBeFalse();
        }

        [Fact]
        public void AClaimIgnoresTheQueryPartJustLikeAConnection()
        {
            // Device identity is scheme+authority+path -- the query carries transport detail that changes
            // under the device (a re-plugged mount moving COM5 -> COM6). A lease keyed any other way would
            // silently free itself the moment the URI was reconciled.
            var hub = NewHub();
            hub.TryAcquireLease(new Uri("Mount://FakeDevice/FakeMount1?port=COM5"), "the imaging session", out _).ShouldBeTrue();

            hub.TryGetLease(new Uri("Mount://FakeDevice/FakeMount1?port=COM6"), out var lease).ShouldBeTrue();
            lease.OwnerLabel.ShouldBe("the imaging session");
        }

        // --- Enforcement at the hub -----------------------------------------------------------------

        [Fact]
        public async Task DisconnectingAnOwnedDeviceIsRefused()
        {
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the imaging session", out _).ShouldBeTrue();

            var ex = await Should.ThrowAsync<DeviceLeasedException>(
                async () => await hub.DisconnectAsync(MountUri, cancellationToken: TestContext.Current.CancellationToken));

            ex.Lease.OwnerLabel.ShouldBe("the imaging session");
            ex.Message.ShouldContain("the imaging session");
        }

        [Fact]
        public async Task ForceDisconnectIsAllowedSoShutdownCanAlwaysBringTheHardwareDown()
        {
            // A device that was never connected disconnects to a no-op; what matters here is that the
            // ownership check does not fire first.
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the imaging session", out _).ShouldBeTrue();

            await hub.DisconnectAsync(MountUri, force: true, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task AnUnownedDeviceDisconnectsAsBefore()
        {
            var hub = NewHub();

            await hub.DisconnectAsync(MountUri, cancellationToken: TestContext.Current.CancellationToken);
        }

        // --- All-or-nothing claim over a whole rig ---------------------------------------------------

        [Fact]
        public void ClaimingARigIsAllOrNothing()
        {
            // Half-owning a rig is the worst outcome: the run would start, drive the mount, and only
            // discover it never had the camera at the first exposure.
            var hub = NewHub();
            hub.TryAcquireLease(CameraUri, "a planetary capture", out _).ShouldBeTrue();

            var ex = Should.Throw<DeviceLeasedException>(
                () => DeviceLeaseSet.Acquire(hub, [MountUri, CameraUri], "the imaging session"));

            ex.Lease.OwnerLabel.ShouldBe("a planetary capture");
            hub.TryGetLease(MountUri, out _).ShouldBeFalse("the mount it did claim must have been given back");
        }

        [Fact]
        public void ClaimingTheSameDeviceTwiceInOneRigIsFine()
        {
            // A single camera can legitimately fill both an OTA slot and the OAG guide-camera slot.
            // Without de-duplication the set would deadlock against its own first claim.
            var hub = NewHub();

            using var leases = DeviceLeaseSet.Acquire(hub, [CameraUri, MountUri, CameraUri], "the imaging session");

            hub.Leases.Count.ShouldBe(2);
        }

        [Fact]
        public void DisposingTheSetReleasesEveryClaimAndIsIdempotent()
        {
            var hub = NewHub();
            var leases = DeviceLeaseSet.Acquire(hub, [MountUri, CameraUri], "the imaging session");

            leases.Dispose();
            leases.Dispose();

            hub.Leases.ShouldBeEmpty();
        }

        [Fact]
        public void AHostWithNoHubClaimsNothingAndStillWorks()
        {
            using var leases = DeviceLeaseSet.Acquire(null, [MountUri], "the imaging session");

            Should.NotThrow(leases.Dispose);
        }

        // --- The shared verdict ----------------------------------------------------------------------

        [Fact]
        public void AnUnownedDeviceIsAllowedForBothActions()
        {
            var hub = NewHub();

            DeviceOwnershipGate.Evaluate(hub, MountUri, DeviceAction.Actuate).Allowed.ShouldBeTrue();
            DeviceOwnershipGate.Evaluate(hub, MountUri, DeviceAction.Disconnect).Allowed.ShouldBeTrue();
            DeviceOwnershipGate.Evaluate(hub, MountUri, DeviceAction.Actuate).Describe().ShouldBeEmpty();
        }

        [Fact]
        public void ANullHubAllowsEverythingSoAHostWithoutOneIsUnaffected()
        {
            DeviceOwnershipGate.Evaluate(null, MountUri, DeviceAction.Disconnect).Allowed.ShouldBeTrue();
            DeviceOwnershipGate.OwnedDevices(null).ShouldBeEmpty();
        }

        [Theory]
        [InlineData(DeviceAction.Disconnect)]
        [InlineData(DeviceAction.Actuate)]
        public void TheRefusalNamesTheDeviceAndTheOwner(DeviceAction action)
        {
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the flat run", out _).ShouldBeTrue();

            var verdict = DeviceOwnershipGate.Evaluate(hub, MountUri, action);

            verdict.Allowed.ShouldBeFalse();
            var described = verdict.Describe();
            described.ShouldContain("Mount (FakeMount1)", Case.Sensitive, "the user needs to know WHICH device");
            described.ShouldContain("the flat run", Case.Sensitive, "and what is holding it");
        }

        [Fact]
        public void TheTwoActionsExplainThemselvesDifferently()
        {
            // Same rule, different instruction: one is "you cannot take it away", the other "you cannot
            // command it". A single shared sentence would be wrong for one of them.
            var hub = NewHub();
            hub.TryAcquireLease(MountUri, "the imaging session", out _).ShouldBeTrue();

            var disconnect = DeviceOwnershipGate.Evaluate(hub, MountUri, DeviceAction.Disconnect).Describe();
            var actuate = DeviceOwnershipGate.Evaluate(hub, MountUri, DeviceAction.Actuate).Describe();

            disconnect.ShouldNotBe(actuate);
            disconnect.ShouldContain("disconnect");
        }

        [Fact]
        public void OwnedDevicesListsEveryClaimForABadgedEquipmentList()
        {
            var hub = NewHub();
            using var leases = DeviceLeaseSet.Acquire(hub, [MountUri, CameraUri], "the imaging session");

            var owned = DeviceOwnershipGate.OwnedDevices(hub);

            owned.Length.ShouldBe(2);
            owned.Select(l => l.DeviceUri).ShouldBe([MountUri, CameraUri], ignoreOrder: true);
        }

        // --- A real run, end to end -----------------------------------------------------------------

        [Fact]
        public async Task ARunRefusesToStartWhenSomethingElseAlreadyOwnsTheRig()
        {
            // Proves the session actually claims its Setup: the only way it can notice a pre-existing
            // owner is by trying to take the same claim.
            using var ctx = await SessionTestHelper.CreateSessionAsync(_output, cancellationToken: TestContext.Current.CancellationToken);
            var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();

            hub.TryAcquireLease(ctx.Session.Setup.Mount.Device.DeviceUri, "a polar alignment", out _).ShouldBeTrue();

            await ctx.Session.RunAsync(TestContext.Current.CancellationToken);

            ctx.Session.Phase.ShouldBe(SessionPhase.Failed);
            ctx.Session.FailureReason.ShouldNotBeNull();
            ctx.Session.FailureReason.ShouldContain("a polar alignment");
        }

        [Fact]
        public async Task AFinishedRunGivesTheRigBack()
        {
            // A leaked lease is invisible until the NEXT run refuses to start -- by which point the user
            // has lost a night and has no idea why. Assert the release explicitly.
            using var ctx = await SessionTestHelper.CreateSessionAsync(_output, cancellationToken: TestContext.Current.CancellationToken);
            var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();

            await ctx.Session.RunAsync(TestContext.Current.CancellationToken);

            hub.Leases.ShouldBeEmpty("every claim must be released however the run ended");
        }

        [Fact]
        public async Task AFlatRunOwnsTheRigJustAsCompletelyAsASession()
        {
            // The case every previous guard missed: FlatsBootstrapper leaves LiveSessionState.IsRunning
            // false, so a UI-flag guard waves a focuser jog / mount pulse / video capture straight through
            // while the flat run is metering.
            using var ctx = await SessionTestHelper.CreateSessionAsync(_output, cancellationToken: TestContext.Current.CancellationToken);
            var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();

            hub.TryAcquireLease(ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri, "a planetary capture", out _).ShouldBeTrue();

            await ctx.Session.RunFlatsOnlyAsync(TwilightPeriod.Dawn, TestContext.Current.CancellationToken);

            ctx.Session.Phase.ShouldBe(SessionPhase.Failed);
            ctx.Session.FailureReason.ShouldNotBeNull();
            ctx.Session.FailureReason.ShouldContain("a planetary capture");
        }
    }
}
