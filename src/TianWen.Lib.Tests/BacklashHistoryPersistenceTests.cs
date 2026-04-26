using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Round-trip tests for the per-focuser backlash sidecar JSON. The sidecar lives at
/// <c>%LOCALAPPDATA%/TianWen/Profiles/BacklashHistory/&lt;focuserDeviceId&gt;.json</c>
/// and holds the EWMA + sample count + timestamp so the next session bootstraps from it.
/// </summary>
public class BacklashHistoryPersistenceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenSavedRecordWhenLoadThenReturnsSameValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        const string focuserId = "FakeFocuser/1";
        var record = new BacklashEstimateRecord(
            EwmaIn: 75,
            EwmaOut: 50,
            Samples: 4,
            LastUpdatedUtc: new DateTimeOffset(2026, 4, 26, 10, 30, 0, TimeSpan.Zero));

        await BacklashHistoryPersistence.SaveAsync(external, focuserId, record, ct);
        var loaded = await BacklashHistoryPersistence.TryLoadAsync(external, focuserId, ct);

        loaded.ShouldNotBeNull();
        loaded.EwmaIn.ShouldBe(75);
        loaded.EwmaOut.ShouldBe(50);
        loaded.Samples.ShouldBe(4);
        loaded.LastUpdatedUtc.ShouldBe(record.LastUpdatedUtc);
    }

    [Fact]
    public async Task GivenNoFileWhenLoadThenReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);

        var loaded = await BacklashHistoryPersistence.TryLoadAsync(external, "MissingFocuser/9", ct);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task GivenSaveOverwritesPriorWhenLoadThenReturnsLatest()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        const string focuserId = "FakeFocuser/2";

        var first = new BacklashEstimateRecord(50, 40, 1, DateTimeOffset.UtcNow.AddHours(-1));
        await BacklashHistoryPersistence.SaveAsync(external, focuserId, first, ct);

        var second = new BacklashEstimateRecord(80, 60, 5, DateTimeOffset.UtcNow);
        await BacklashHistoryPersistence.SaveAsync(external, focuserId, second, ct);

        var loaded = await BacklashHistoryPersistence.TryLoadAsync(external, focuserId, ct);

        loaded.ShouldNotBeNull();
        loaded.EwmaIn.ShouldBe(80);
        loaded.Samples.ShouldBe(5);
    }

    [Fact]
    public async Task GivenMultipleFocusersWhenSaveThenIsolatedByDeviceId()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);

        var recordA = new BacklashEstimateRecord(70, 50, 3, DateTimeOffset.UtcNow);
        var recordB = new BacklashEstimateRecord(20, 15, 7, DateTimeOffset.UtcNow);
        await BacklashHistoryPersistence.SaveAsync(external, "FocuserA/1", recordA, ct);
        await BacklashHistoryPersistence.SaveAsync(external, "FocuserB/1", recordB, ct);

        var loadedA = await BacklashHistoryPersistence.TryLoadAsync(external, "FocuserA/1", ct);
        var loadedB = await BacklashHistoryPersistence.TryLoadAsync(external, "FocuserB/1", ct);

        loadedA.ShouldNotBeNull();
        loadedA.EwmaIn.ShouldBe(70);
        loadedB.ShouldNotBeNull();
        loadedB.EwmaIn.ShouldBe(20);
    }
}
