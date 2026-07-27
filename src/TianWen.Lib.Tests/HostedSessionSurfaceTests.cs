using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The host-side state the P2 endpoints sit on: the prompt hand-off, the pushed schedule, and the
    /// notification ring. Exercised directly rather than through HTTP -- these are the parts with real
    /// logic (grab-and-clear, drain semantics, ring eviction); the endpoints around them are routing.
    /// </summary>
    public class HostedSessionSurfaceTests
    {
        private static HostedSession CreateHost() => new HostedSession(Substitute.For<ISessionFactory>());

        private static SessionPromptEventArgs Prompt(TaskCompletionSource<bool> completion)
            => new SessionPromptEventArgs("Manual flat panel", "Switch it on, then Continue.", "Continue", "Cancel", completion);

        // --- Prompts -------------------------------------------------------------------------------

        [Fact]
        public void RespondingWithNoPromptOutstandingReportsFailureRatherThanThrowing()
        {
            // A stale client retry, or a race with the session cancelling its own prompt, must not 500.
            var host = CreateHost();

            host.PendingPrompt.ShouldBeNull();
            host.TryRespondToPrompt(true).ShouldBeFalse();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RespondingForwardsTheAnswerToTheWaitingSession(bool proceed)
        {
            var host = CreateHost();
            var completion = new TaskCompletionSource<bool>();
            host.SetPendingPrompt(Prompt(completion));

            host.PendingPrompt.ShouldNotBeNull();
            host.TryRespondToPrompt(proceed).ShouldBeTrue();

            completion.Task.IsCompletedSuccessfully.ShouldBeTrue();
            (await completion.Task).ShouldBe(proceed);
        }

        [Fact]
        public async Task OnlyTheFirstResponseWins()
        {
            // Grab-and-clear: the auto-proceed timer and a real client can race, and the second one to
            // arrive must be told there was nothing to answer rather than appearing to succeed.
            var host = CreateHost();
            var completion = new TaskCompletionSource<bool>();
            host.SetPendingPrompt(Prompt(completion));

            host.TryRespondToPrompt(true).ShouldBeTrue();
            host.TryRespondToPrompt(false).ShouldBeFalse();

            host.PendingPrompt.ShouldBeNull();
            (await completion.Task).ShouldBeTrue();
        }

        // --- Pushed schedule -----------------------------------------------------------------------

        [Fact]
        public void ThePushedScheduleDrainsOnceAndThenIsEmpty()
        {
            var host = CreateHost();
            host.PendingSchedule.ShouldBeEmpty();

            host.SetSchedule([MakeObservation("M42"), MakeObservation("M45")]);
            host.PendingSchedule.Length.ShouldBe(2);

            var drained = host.DrainSchedule();
            drained.Length.ShouldBe(2);
            drained[0].Target.Name.ShouldBe("M42");

            // A second start must not silently re-run last night's schedule.
            host.DrainSchedule().ShouldBeEmpty();
            host.PendingSchedule.ShouldBeEmpty();
        }

        [Fact]
        public void ADefaultScheduleArrayIsNormalisedToEmpty()
        {
            // ImmutableArray<T> is a struct, so a caller can hand over `default` -- which throws on
            // enumeration. Normalising on the way in keeps every reader from having to check.
            var host = CreateHost();

            host.SetSchedule(default);

            host.PendingSchedule.IsDefault.ShouldBeFalse();
            host.PendingSchedule.ShouldBeEmpty();
        }

        // --- Notification ring ---------------------------------------------------------------------

        [Fact]
        public void NotificationsAreKeptOldestFirstAndEvictTheOldestOnceFull()
        {
            var host = CreateHost();

            // One more than the ring holds, so the first entry must have fallen out.
            const int capacity = 200;
            for (var i = 0; i < capacity + 5; i++)
            {
                host.AddNotification(new NotificationDto
                {
                    Severity = "Info",
                    Message = $"event {i}",
                    TimestampUtc = new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero).AddSeconds(i),
                });
            }

            var notifications = host.Notifications;
            notifications.Length.ShouldBe(capacity);
            notifications[0].Message.ShouldBe("event 5");
            notifications[^1].Message.ShouldBe($"event {capacity + 4}");
        }

        private static ScheduledObservation MakeObservation(string name) => new ScheduledObservation(
            new Target(5.588, -5.39, name, null),
            new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(45),
            AcrossMeridian: false,
            FilterPlan: [],
            Gain: null,
            Offset: null);
    }

    /// <summary>
    /// The schedule wire contract. This DTO exists purely to carry fidelity that
    /// <see cref="PendingTarget"/> drops, so the tests are about what survives a round trip.
    /// </summary>
    public class ScheduledObservationDtoTests
    {
        [Fact]
        public void TheFullObservationSurvivesARoundTrip()
        {
            var original = new ScheduledObservation(
                new Target(5.588, -5.39, "M42", CatalogIndex.NGC1976),
                new DateTimeOffset(2026, 7, 26, 21, 15, 0, TimeSpan.Zero),
                TimeSpan.FromMinutes(90),
                AcrossMeridian: true,
                FilterPlan:
                [
                    new FilterExposure(0, TimeSpan.FromSeconds(120), 20),
                    new FilterExposure(2, TimeSpan.FromSeconds(300), 8),
                ],
                Gain: 100,
                Offset: 30,
                Priority: ObservationPriority.High);

            var restored = ScheduledObservationDto.FromScheduled(original).ToScheduled();

            restored.Target.Name.ShouldBe("M42");
            restored.Target.RA.ShouldBe(5.588);
            restored.Target.Dec.ShouldBe(-5.39);
            restored.Target.CatalogIndex.ShouldBe(CatalogIndex.NGC1976);

            // The three fields PendingTarget cannot express, which is the entire reason this type exists.
            restored.Start.ShouldBe(original.Start);
            restored.AcrossMeridian.ShouldBeTrue();
            restored.FilterPlan.Length.ShouldBe(2);
            restored.FilterPlan[1].FilterPosition.ShouldBe(2);
            restored.FilterPlan[1].SubExposure.ShouldBe(TimeSpan.FromSeconds(300));
            restored.FilterPlan[1].Count.ShouldBe(8);

            restored.Duration.ShouldBe(TimeSpan.FromMinutes(90));
            restored.Gain.ShouldBe(100);
            restored.Offset.ShouldBe(30);
            restored.Priority.ShouldBe(ObservationPriority.High);
        }

        [Fact]
        public void ASynthesizedTargetWithoutCoordinatesStaysWireSafe()
        {
            // A name-only target carries NaN coordinates, which JSON cannot represent -- unguarded it
            // would take down the whole response (see JsonNumber).
            var original = new ScheduledObservation(
                new Target(double.NaN, double.NaN, "Unnamed field", null),
                new DateTimeOffset(2026, 7, 26, 21, 15, 0, TimeSpan.Zero),
                TimeSpan.FromMinutes(30),
                AcrossMeridian: false,
                FilterPlan: [],
                Gain: null,
                Offset: null);

            var dto = ScheduledObservationDto.FromScheduled(original);

            if (!JsonNumber.WireAllowsNonFinite)
            {
                double.IsFinite(dto.TargetRA).ShouldBeTrue();
                double.IsFinite(dto.TargetDec).ShouldBeTrue();
            }

            dto.CatalogIndex.ShouldBeNull();
            dto.FilterPlan.ShouldBeEmpty();
            dto.ToScheduled().Target.Name.ShouldBe("Unnamed field");
        }

        [Fact]
        public void AnEmptyFilterPlanRoundTripsAsEmptyRatherThanDefault()
        {
            var dto = new ScheduledObservationDto
            {
                TargetName = "M31",
                TargetRA = 0.712,
                TargetDec = 41.27,
                Start = new DateTimeOffset(2026, 7, 26, 22, 0, 0, TimeSpan.Zero),
                DurationMinutes = 60,
                AcrossMeridian = false,
                FilterPlan = default,
                Priority = ObservationPriority.Normal,
            };

            var restored = dto.ToScheduled();

            restored.FilterPlan.IsDefault.ShouldBeFalse();
            restored.FilterPlan.ShouldBeEmpty();
        }

        [Fact]
        public void OmittingTheOptionalFieldsYieldsAUsableObservation()
        {
            // A hand-written caller should be able to post the minimum. Priority in particular travels as
            // an ordinal (this contract applies no string-enum conversion), so defaulting it means a
            // caller never has to guess the number.
            var dto = new ScheduledObservationDto
            {
                TargetName = "M31",
                TargetRA = 0.712,
                TargetDec = 41.27,
                Start = new DateTimeOffset(2026, 7, 26, 22, 0, 0, TimeSpan.Zero),
                DurationMinutes = 60,
                AcrossMeridian = false,
            };

            dto.Priority.ShouldBe(ObservationPriority.Normal);
            dto.FilterPlan.IsDefault.ShouldBeFalse();

            var restored = dto.ToScheduled();
            restored.Priority.ShouldBe(ObservationPriority.Normal);
            restored.Target.Name.ShouldBe("M31");
            restored.FilterPlan.ShouldBeEmpty();
        }
    }
}
