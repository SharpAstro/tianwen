using System.Text.Json;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A field a caller leaves out takes the DECLARED default, never its type's. The JSON source generator gives an init-only
/// property its type's default when the field is absent, whatever its initializer says, so a dark library or a preview
/// posted without a binning arrived at bin 0 and was refused, a pushed schedule without a priority ran at High (the
/// enum's 0), and an array or a list came back null or default. Every such property is <c>set</c> now; each test posts
/// the smallest body its type takes.
/// </summary>
public class WireDefaultsTests
{
    private static T Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
        => JsonSerializer.Deserialize(json, info).ShouldNotBeNull();

    [Fact]
    public void ADarkLibraryWithoutABinningIsTakenAtBinOne()
        => Read("""{"deviceUri":"fake://camera/1","exposureSeconds":1,"count":1}""", HostingJsonContext.Default.DarkLibraryRequestDto)
            .Bin.ShouldBe(1);

    [Fact]
    public void APreviewWithoutABinningIsTakenAtBinOne()
        => Read("""{"exposureSeconds":1}""", HostingJsonContext.Default.PreviewExposureRequestDto).Binning.ShouldBe(1);

    [Fact]
    public void AScheduledObservationWithoutAPriorityOrAPlanIsNormalWithAnEmptyPlan()
    {
        var observation = Read(
            """{"targetName":"M 42","targetRA":5.59,"targetDec":-5.39,"start":"2026-09-27T12:00:00+00:00","durationMinutes":60,"acrossMeridian":false}""",
            HostingJsonContext.Default.ScheduledObservationDto);

        observation.Priority.ShouldBe(ObservationPriority.Normal, "High is the enum's 0, which an absent priority used to become");
        observation.FilterPlan.IsDefault.ShouldBeFalse();
        observation.FilterPlan.ShouldBeEmpty();
    }

    [Fact]
    public void AStateWithoutItsListsReadsBackEmptyLists()
    {
        Read("""{"camera":"Fake","deviceUri":"fake://camera/1"}""", HostingJsonContext.Default.DarkLibraryStateDto).Frames.ShouldBeEmpty();
        Read("{}", HostingJsonContext.Default.NodeRecoveryDto).Devices.ShouldBeEmpty();
        Read("{}", HostingJsonContext.Default.PolarOverlayDto).RingRadiiArcmin.ShouldBeEmpty();
    }

    [Fact]
    public void AJournalWithoutItsListsOrVersionReadsBackTheirDefaults()
    {
        var journal = Read("""{"writtenUtc":"2026-09-27T12:00:00+00:00","processId":42}""", NodeJournalJsonContext.Default.NodeJournal);

        journal.Version.ShouldBe(NodeJournal.CurrentVersion);
        journal.Devices.IsDefault.ShouldBeFalse();
        journal.Crashes.IsDefault.ShouldBeFalse();
    }
}
