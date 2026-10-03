using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stored profile's revision (<see cref="StoredProfile"/>), which the node's one profile writer compares an edit
/// against (P3 part 1 of docs/plans/hardware-in-the-server.md, #930): the hash of the file's bytes, so it follows what is
/// stored and nothing else.
/// </summary>
public class ProfileRevisionTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly Guid Id = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000931");

    private static ProfileData Rig(string camera) => new ProfileData(
        new Uri("Mount://FakeDevice/FakeMount1"), NoneDevice.Instance.DeviceUri,
        [new OTAData("Main", 800, new Uri($"Camera://FakeDevice/{camera}"), null, null, null, null, null)]);

    private FakeExternal External() => new FakeExternal(output, _folders.Create("tw_rev_"));

    [Fact]
    public async Task WhatIsReadBackHasTheRevisionItWasSavedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = External();
        var profile = new Profile(Id, "Rig", Rig("FakeCamera1"));

        var saved = await profile.SaveStoredAsync(external, ct);
        var read = (await Profile.TryReadStoredAsync(external, Id, ct)).ShouldNotBeNull();

        read.Revision.ShouldBe(saved.Revision);
        saved.Revision.ShouldBe(profile.ComputeRevision());
        read.Profile.DisplayName.ShouldBe("Rig");
        read.Profile.ProfileId.ShouldBe(Id);
    }

    // Two copies of the same profile hold different telescope arrays, which record equality compares by reference; the
    // revision is what a writer compares, and it must see them as the same.
    [Fact]
    public void ARevisionFollowsTheContentNotTheInstance()
    {
        new Profile(Id, "Rig", Rig("FakeCamera1")).ComputeRevision().ShouldBe(new Profile(Id, "Rig", Rig("FakeCamera1")).ComputeRevision());
        new Profile(Id, "Rig", Rig("FakeCamera2")).ComputeRevision().ShouldNotBe(new Profile(Id, "Rig", Rig("FakeCamera1")).ComputeRevision());
        new Profile(Id, "Renamed", Rig("FakeCamera1")).ComputeRevision().ShouldNotBe(new Profile(Id, "Rig", Rig("FakeCamera1")).ComputeRevision());
    }

    // Another process's save (the GUI's, until P6) moves the revision, which is what refuses an edit made before it.
    [Fact]
    public async Task AnotherSaveMovesTheRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = External();
        var before = await new Profile(Id, "Rig", Rig("FakeCamera1")).SaveStoredAsync(external, ct);

        await new Profile(Id, "Rig", Rig("FakeCamera2")).SaveAsync(external, ct);

        (await Profile.TryReadStoredAsync(external, Id, ct)).ShouldNotBeNull().Revision.ShouldNotBe(before.Revision);
    }

    [Fact]
    public async Task AMissingOrUnreadableProfileReadsAsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = External();

        (await Profile.TryReadStoredAsync(external, Id, ct)).ShouldBeNull();

        external.ProfileFolder.Create();
        await File.WriteAllTextAsync(Path.Combine(external.ProfileFolder.FullName, Profile.DeviceIdFromUUID(Id) + ".json"), "{ not json", ct);
        (await Profile.TryReadStoredAsync(external, Id, ct)).ShouldBeNull();
    }
}
