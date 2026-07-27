using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
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
    }
}
