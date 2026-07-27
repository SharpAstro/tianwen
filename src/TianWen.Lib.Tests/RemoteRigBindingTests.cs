using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the remote-rig binding record and registry (docs/plans/remote-profile.md P4).
    /// <para>
    /// The invariant worth protecting: a binding is keyed on the rig's <b>stable node id</b>, never on
    /// its address or display name. A rig moves between DHCP leases and gets renamed; neither may orphan
    /// the binding, and an offline rig must stay listed rather than looking like the binding was lost.
    /// </para>
    /// </summary>
    public class RemoteRigBindingTests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        /// <summary>
        /// A <see cref="FakeExternal"/> over a folder unique to this call.
        /// <para>
        /// <b>Not the default root.</b> <c>FakeExternal</c>'s default temp folder is keyed on the test
        /// name plus the DATE, so it is reused by every run on the same day -- fine for tests that only
        /// write, fatal for these, which assert on how many records <c>LoadAllAsync</c> finds. Yesterday's
        /// bindings would be counted as today's. (Caught exactly that way: green in isolation, red in the
        /// full suite after an earlier run had seeded the folder.)
        /// </para>
        /// </summary>
        private FakeExternal FreshExternal() => new FakeExternal(
            _output,
            new FakeTimeProviderWrapper(DateTimeOffset.UnixEpoch),
            root: Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tw-rigbind-{Guid.NewGuid():N}")));

        private static RemoteRigBinding Binding(string alias = "Observatory", string? nodeId = null, string? address = null) =>
            new RemoteRigBinding
            {
                BindingId = Guid.NewGuid(),
                NodeId = nodeId ?? "019f93aa-node",
                Alias = alias,
                LastAddress = address,
            };

        // --- Persistence -----------------------------------------------------------------------------

        [Fact]
        public async Task ABindingRoundTripsThroughDisk()
        {
            var external = FreshExternal();
            var binding = Binding(address: "http://192.168.1.50:1888/") with { RemoteProfileId = Guid.NewGuid() };

            await RemoteRigPersistence.SaveAsync(binding, external, TestContext.Current.CancellationToken);
            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);

            loaded.Length.ShouldBe(1);
            loaded[0].ShouldBe(binding);
        }

        [Fact]
        public async Task TheLastSeenStampRoundTripsThroughDisk()
        {
            var external = FreshExternal();
            var seen = new DateTimeOffset(2026, 7, 26, 21, 34, 0, TimeSpan.Zero);
            var binding = Binding(address: "http://192.168.1.50:1888/") with { LastSeenUtc = seen };

            await RemoteRigPersistence.SaveAsync(binding, external, TestContext.Current.CancellationToken);
            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);

            loaded.Length.ShouldBe(1);
            loaded[0].LastSeenUtc.ShouldBe(seen);
        }

        [Fact]
        public async Task ABindingWrittenBeforeTheStampExistedStillLoads()
        {
            // The back-compat guard for adding a field to a persisted record: a file from an earlier
            // build has no lastSeenUtc at all. It must load as "never seen" rather than failing the
            // parse and taking the rig's binding with it.
            var external = FreshExternal();
            var folder = Directory.CreateDirectory(
                Path.Combine(external.ProfileFolder.FullName, RemoteRigPersistence.FolderName));
            // PascalCase, matching what RemoteRigJsonContext actually writes -- it configures no naming
            // policy, and the reader is case-SENSITIVE, so a camelCase fixture would fail to bind the
            // required members and be skipped as corrupt, passing this test for the wrong reason.
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(
                Path.Combine(folder.FullName, $"{id}.json"),
                $$"""
                {"BindingId":"{{id}}","NodeId":"019f93aa-node","RemoteProfileId":null,"Alias":"Observatory","LastAddress":"http://10.0.0.4:1888/"}
                """,
                TestContext.Current.CancellationToken);

            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);

            loaded.Length.ShouldBe(1);
            loaded[0].Alias.ShouldBe("Observatory");
            loaded[0].LastSeenUtc.ShouldBeNull();
        }

        [Fact]
        public async Task NoBindingsFolderIsNotAnError()
        {
            // First run: nothing has ever been bound.
            var external = FreshExternal();

            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);

            loaded.ShouldBeEmpty();
        }

        [Fact]
        public async Task OneCorruptRecordDoesNotHideTheOthers()
        {
            // One file per binding exists precisely so a bad write costs one rig, not every rig.
            var external = FreshExternal();
            await RemoteRigPersistence.SaveAsync(Binding("Alpha"), external, TestContext.Current.CancellationToken);
            await RemoteRigPersistence.SaveAsync(Binding("Beta"), external, TestContext.Current.CancellationToken);

            var folder = Path.Combine(external.ProfileFolder.FullName, RemoteRigPersistence.FolderName);
            await File.WriteAllTextAsync(Path.Combine(folder, $"{Guid.NewGuid()}.json"), "{ this is not json",
                TestContext.Current.CancellationToken);

            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);

            loaded.Length.ShouldBe(2);
            loaded.Select(b => b.Alias).ShouldBe(["Alpha", "Beta"], "and the order stays alias-stable");
        }

        [Fact]
        public async Task DeletingABindingLeavesTheRest()
        {
            var external = FreshExternal();
            var keep = Binding("Alpha");
            var drop = Binding("Beta");
            await RemoteRigPersistence.SaveAsync(keep, external, TestContext.Current.CancellationToken);
            await RemoteRigPersistence.SaveAsync(drop, external, TestContext.Current.CancellationToken);

            RemoteRigPersistence.Delete(drop.BindingId, external, null);

            var loaded = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);
            loaded.Length.ShouldBe(1);
            loaded[0].BindingId.ShouldBe(keep.BindingId);
        }

        [Fact]
        public void DeletingAMissingBindingIsHarmless()
        {
            // Unbinding twice (a double-click, or a stale UI) must not throw.
            var external = FreshExternal();

            Should.NotThrow(() => RemoteRigPersistence.Delete(Guid.NewGuid(), external, null));
        }

        // --- Address resolution ----------------------------------------------------------------------

        [Fact]
        public void AnAddressHintIsUsedWhenDiscoveryHasNotSeenTheRig()
        {
            // A rig that has not beaconed yet this run is still worth an optimistic connect.
            var binding = Binding(address: "http://192.168.1.50:1888/");

            RemoteRigConnection.ResolveAddress(binding, peers: null)
                .ShouldBe(new Uri("http://192.168.1.50:1888/"));
        }

        [Fact]
        public void ARigWithNoHintAndNoDiscoveryIsUnreachable()
        {
            RemoteRigConnection.ResolveAddress(Binding(), peers: null).ShouldBeNull();
        }

        [Fact]
        public void AGarbageHintIsTreatedAsNoHintRatherThanThrowing()
        {
            // A hand-edited or truncated record must degrade to "offline", not crash the picker.
            RemoteRigConnection.ResolveAddress(Binding(address: "not a url"), peers: null).ShouldBeNull();
        }

        // --- Registry --------------------------------------------------------------------------------

        [Fact]
        public void UpsertReplacesByBindingIdRatherThanAppending()
        {
            var registry = new RemoteRigRegistry();
            var original = Binding("Observatory", address: null);
            registry.SetBindings([original]);

            registry.Upsert(original with { LastAddress = "http://10.0.0.9:1888/" });

            registry.Bindings.Length.ShouldBe(1);
            registry.Bindings[0].LastAddress.ShouldBe("http://10.0.0.9:1888/");
        }

        [Fact]
        public void UpsertAddsAnUnknownBinding()
        {
            var registry = new RemoteRigRegistry();
            registry.SetBindings([Binding("Alpha")]);

            registry.Upsert(Binding("Beta"));

            registry.Bindings.Length.ShouldBe(2);
        }

        [Fact]
        public void RemovingABindingWithNoConnectionReturnsNull()
        {
            var registry = new RemoteRigRegistry();
            var binding = Binding();
            registry.SetBindings([binding]);

            registry.Remove(binding.BindingId).ShouldBeNull();
            registry.Bindings.ShouldBeEmpty();
        }

        [Fact]
        public void ADefaultBindingArrayIsTreatedAsEmpty()
        {
            // ImmutableArray's default is not the empty array; a caller handing one over must not make
            // every later read throw.
            var registry = new RemoteRigRegistry();

            registry.SetBindings(default);

            registry.Bindings.ShouldBeEmpty();
            registry.IsConnected(Guid.NewGuid()).ShouldBeFalse();
        }

        // --- "last seen" wording ---------------------------------------------------------------------

        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 27, 6, 0, 0, TimeSpan.Zero);

        [Theory]
        [InlineData(0, "moments ago")]
        [InlineData(30, "moments ago")]
        [InlineData(60, "1 min ago")]
        [InlineData(12 * 60, "12 min ago")]
        [InlineData(60 * 60, "1 h ago")]
        [InlineData(3 * 60 * 60 + 59 * 60, "3 h ago")]
        [InlineData(24 * 60 * 60, "1 day ago")]
        [InlineData(5 * 24 * 60 * 60, "5 days ago")]
        public void AnAgeReadsTheWayAPersonWouldSayIt(int secondsAgo, string expected) =>
            RemoteRigActions.FormatAge(Now - TimeSpan.FromSeconds(secondsAgo), Now).ShouldBe(expected);

        [Fact]
        public void AClockThatWentBackwardsDoesNotProduceANegativeAge()
        {
            // An NTP correction or a restored VM can put the stamp in the future. "-2 h ago" would be
            // worse than useless, and it is not a state worth a separate message.
            RemoteRigActions.FormatAge(Now + TimeSpan.FromHours(2), Now).ShouldBe("moments ago");
        }

        [Fact]
        public void TheOfflineTailPrefersTheAgeOverTheAddress()
        {
            var binding = Binding(address: "http://10.0.0.4:1888/") with { LastSeenUtc = Now.AddHours(-6) };

            RemoteRigActions.DescribeLastSeen(binding, Now).ShouldBe(" (last seen 6 h ago)");
        }

        [Fact]
        public void TheOfflineTailFallsBackToTheAddressWhenTheRigWasNeverReached()
        {
            // A binding minted from an announcement that was never actually answered: the address is all
            // we have, and it is still better than nothing.
            var binding = Binding(address: "http://10.0.0.4:1888/");

            RemoteRigActions.DescribeLastSeen(binding, Now).ShouldBe(" (last seen at http://10.0.0.4:1888/)");
        }

        [Fact]
        public void TheOfflineTailIsEmptyWhenThereIsNothingToReport() =>
            RemoteRigActions.DescribeLastSeen(Binding(), Now).ShouldBe("");

        // --- Connect-all sweep -----------------------------------------------------------------------

        /// <summary>
        /// A <b>real</b> clock for the sweep tests, deliberately not <c>FakeTimeProviderWrapper</c>.
        /// <para>
        /// A started mirror sleeps between polls via <c>ITimeProvider.SleepAsync</c>, and the fake
        /// provider's <c>SleepAsync</c> auto-advances -- so with a fake clock the poll loop would
        /// busy-spin against a dead endpoint for as long as the test lived. On a real clock the idle
        /// interval is 2 s, so each swept rig polls at most once before the test disposes it.
        /// </para>
        /// </summary>
        private static ITimeProvider RealClock() => new SystemTimeProvider();

        /// <summary>
        /// An address that fails to connect immediately rather than hanging: port 1 on loopback refuses,
        /// so a poll fails fast instead of waiting out the request budget.
        /// </summary>
        private const string RefusedAddress = "http://127.0.0.1:1/";

        [Fact]
        public async Task SweepingWithNothingBoundDoesNothing()
        {
            var rigs = new RemoteRigRegistry();
            var contexts = new ViewContexts();

            var outcome = await RemoteRigActions.ConnectAllAsync(
                rigs, contexts, new GuiAppState(), FreshExternal(), RealClock(), NullLogger.Instance,
                TestContext.Current.CancellationToken);

            outcome.ShouldBe(new RemoteRigActions.ConnectAllOutcome(0, 0));
            outcome.DidAnything.ShouldBeFalse();
            rigs.Connections.ShouldBeEmpty();
            contexts.All.Length.ShouldBe(1); // still just the local context
        }

        [Fact]
        public async Task ARigWithNoReachableAddressIsRecordedAsOfflineRatherThanDropped()
        {
            // No peer table and no address hint: nothing to talk to. The binding must survive anyway --
            // "I own this rig and it is not answering" is information.
            var external = FreshExternal();
            var rigs = new RemoteRigRegistry();
            rigs.SetBindings([Binding(alias: "Shed")]);

            var outcome = await RemoteRigActions.ConnectAllAsync(
                rigs, new ViewContexts(), new GuiAppState(), external, RealClock(), NullLogger.Instance,
                TestContext.Current.CancellationToken);

            outcome.ShouldBe(new RemoteRigActions.ConnectAllOutcome(Connected: 0, Offline: 1));
            rigs.Connections.ShouldBeEmpty();
            rigs.Bindings.Length.ShouldBe(1);

            var persisted = await RemoteRigPersistence.LoadAllAsync(external, null, TestContext.Current.CancellationToken);
            persisted.Length.ShouldBe(1);
            persisted[0].Alias.ShouldBe("Shed");
            persisted[0].LastSeenUtc.ShouldBeNull(); // never answered, so no stamp is invented
        }

        [Fact]
        public async Task SweepingStartsMirrorsWithoutChangingWhatIsOnScreen()
        {
            // THE invariant of the sweep: connecting is decoupled from looking. SelectAsync activates;
            // this must not, or opening the board would hijack the view.
            var rigs = new RemoteRigRegistry();
            var contexts = new ViewContexts();
            rigs.SetBindings([
                Binding(alias: "Pier A", nodeId: "node-a", address: RefusedAddress),
                Binding(alias: "Pier B", nodeId: "node-b", address: RefusedAddress),
            ]);

            try
            {
                var outcome = await RemoteRigActions.ConnectAllAsync(
                    rigs, contexts, new GuiAppState(), FreshExternal(), RealClock(), NullLogger.Instance,
                    TestContext.Current.CancellationToken);

                outcome.ShouldBe(new RemoteRigActions.ConnectAllOutcome(Connected: 2, Offline: 0));
                rigs.Connections.Count.ShouldBe(2);

                contexts.Active.ShouldBeSameAs(contexts.Local);
                contexts.IsRemoteActive.ShouldBeFalse();
                contexts.All.Length.ShouldBe(3); // local + one per rig, all pollable, none active
            }
            finally
            {
                await DisposeAllAsync(rigs);
            }
        }

        [Fact]
        public async Task ASweptRigFetchesNoPreviews()
        {
            // N mirrors each pulling JPEGs is the failure mode the opt-in exists to prevent, so the
            // sweep must leave Previews unset.
            var rigs = new RemoteRigRegistry();
            rigs.SetBindings([Binding(alias: "Pier A", address: RefusedAddress)]);

            try
            {
                await RemoteRigActions.ConnectAllAsync(
                    rigs, new ViewContexts(), new GuiAppState(), FreshExternal(), RealClock(), NullLogger.Instance,
                    TestContext.Current.CancellationToken);

                rigs.Connections.Count.ShouldBe(1);
                rigs.Connections.Single().Value.Mirror.Previews.ShouldBeNull();
            }
            finally
            {
                await DisposeAllAsync(rigs);
            }
        }

        [Fact]
        public async Task SweepingAgainLeavesAnAlreadyMirroredRigAlone()
        {
            // The board can re-open, and a second sweep must not tear down and rebuild live mirrors.
            var rigs = new RemoteRigRegistry();
            var contexts = new ViewContexts();
            var external = FreshExternal();
            var clock = RealClock();
            rigs.SetBindings([Binding(alias: "Pier A", address: RefusedAddress)]);

            try
            {
                await RemoteRigActions.ConnectAllAsync(
                    rigs, contexts, new GuiAppState(), external, clock, NullLogger.Instance, TestContext.Current.CancellationToken);
                var first = rigs.Connections.Single().Value;

                var second = await RemoteRigActions.ConnectAllAsync(
                    rigs, contexts, new GuiAppState(), external, clock, NullLogger.Instance, TestContext.Current.CancellationToken);

                second.ShouldBe(new RemoteRigActions.ConnectAllOutcome(0, 0));
                second.DidAnything.ShouldBeFalse();
                rigs.Connections.Count.ShouldBe(1);
                rigs.Connections.Single().Value.ShouldBeSameAs(first); // same mirror, not a replacement
            }
            finally
            {
                await DisposeAllAsync(rigs);
            }
        }

        // --- A binding that cannot be written ---------------------------------------------------------

        /// <summary>
        /// Makes every binding write fail, by parking a <b>file</b> where the bindings folder has to be
        /// created -- <c>Directory.CreateDirectory</c> throws on a name a file already owns.
        /// <para>
        /// Deterministic and permission-free, unlike revoking write access, which needs a different
        /// mechanism per OS and can silently no-op for an elevated CI account.
        /// </para>
        /// </summary>
        private static void BlockBindingWrites(FakeExternal external)
        {
            Directory.CreateDirectory(external.ProfileFolder.FullName);
            File.WriteAllText(
                Path.Combine(external.ProfileFolder.FullName, RemoteRigPersistence.FolderName), "not a folder");
        }

        [Fact]
        public async Task RigsWhoseBindingsCannotBeSavedAreStillMirroredAndStillReported()
        {
            // Best-effort must not mean silent: the sweep runs unattended at startup, so a swallowed
            // write failure would surface only as rigs quietly missing from the picker days later.
            var external = FreshExternal();
            BlockBindingWrites(external);
            var rigs = new RemoteRigRegistry();
            rigs.SetBindings([
                Binding(alias: "Pier A", nodeId: "node-a", address: RefusedAddress),
                Binding(alias: "Pier B", nodeId: "node-b", address: RefusedAddress),
            ]);

            try
            {
                var outcome = await RemoteRigActions.ConnectAllAsync(
                    rigs, new ViewContexts(), new GuiAppState(), external, RealClock(), NullLogger.Instance,
                    TestContext.Current.CancellationToken);

                // The failed write costs neither rig its mirror ...
                outcome.Connected.ShouldBe(2);
                outcome.Offline.ShouldBe(0);
                rigs.Connections.Count.ShouldBe(2);

                // ... and both are named, because "which rigs" is the actionable part.
                outcome.Unsaved.ShouldBe(["Pier A", "Pier B"]);
                outcome.DescribeUnsaved().ShouldBe(
                    "Could not save 2 rig bindings (Pier A, Pier B): they will not reappear after a restart (see the log).");
            }
            finally
            {
                await DisposeAllAsync(rigs);
            }
        }

        [Fact]
        public async Task ACleanSweepHasNothingToReport()
        {
            var rigs = new RemoteRigRegistry();
            rigs.SetBindings([Binding(alias: "Pier A", address: RefusedAddress)]);

            try
            {
                var outcome = await RemoteRigActions.ConnectAllAsync(
                    rigs, new ViewContexts(), new GuiAppState(), FreshExternal(), RealClock(), NullLogger.Instance,
                    TestContext.Current.CancellationToken);

                // IsDefaultOrEmpty, not ShouldBeEmpty: nothing-to-report is carried as `default`, which
                // cannot be enumerated at all -- which is exactly why DescribeUnsaved does this check
                // for its callers.
                outcome.Unsaved.IsDefaultOrEmpty.ShouldBeTrue();
                outcome.DescribeUnsaved().ShouldBeNull("a notification per successful startup would be noise");
            }
            finally
            {
                await DisposeAllAsync(rigs);
            }
        }

        [Fact]
        public async Task SelectingARigWhoseBindingCannotBeSavedStillWatchesItAndWarns()
        {
            var external = FreshExternal();
            BlockBindingWrites(external);
            var rigs = new RemoteRigRegistry();
            var contexts = new ViewContexts();
            rigs.SetBindings([Binding(alias: "Shed", address: RefusedAddress)]);

            try
            {
                // No peer table, so the alias resolves against the bindings already on disk -- the path a
                // rig that is not announcing right now takes.
                var outcome = await RemoteRigActions.SelectAsync(
                    "Shed", rigs, contexts, new GuiAppState(), external, RealClock(), NullLogger.Instance,
                    TestContext.Current.CancellationToken);

                outcome.Severity.ShouldBe(NotificationSeverity.Warning);
                outcome.Message.ShouldBe(
                    $"Watching Shed at {RefusedAddress}. Could not save the binding for Shed: "
                    + "it will not reappear after a restart (see the log).");

                contexts.IsRemoteActive.ShouldBeTrue("a failed write must not cost the user the view they asked for");
            }
            finally
            {
                contexts.Activate(contexts.Local);
                await DisposeAllAsync(rigs);
            }
        }

        /// <summary>Stops every mirror the test started, so no poll loop outlives the test.</summary>
        private static async Task DisposeAllAsync(RemoteRigRegistry rigs)
        {
            foreach (var (bindingId, _) in rigs.Connections)
            {
                if (rigs.Detach(bindingId) is { } connection)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }
}
