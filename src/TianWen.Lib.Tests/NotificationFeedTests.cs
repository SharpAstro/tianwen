using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #2366: the notification feed takes entries from more than one thread (a handler on the UI thread, a discovery
/// finishing on a pool thread), and an entry recorded while another is being recorded must not be lost. On CI the
/// loss showed as <c>GuiContextGatingTests.ASolveAndSyncWithARemoteRigOnScreenDoesNotTouchTheLocalRig</c> failing one
/// run in many: the refusal's warning was gone and "Found 27 devices" was the only entry.
/// </summary>
public class NotificationFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheNewestEntryIsFirstAndPastTheCapTheOldestGo()
    {
        var feed = ImmutableArray<NotificationEntry>.Empty;
        for (var i = 0; i < GuiAppState.MaxNotifications + 3; i++)
        {
            feed = GuiAppState.WithNotification(feed, new NotificationEntry(Now.AddSeconds(i), NotificationSeverity.Info, $"note {i}"));
        }

        feed.Length.ShouldBe(GuiAppState.MaxNotifications);
        feed[0].Message.ShouldBe($"note {GuiAppState.MaxNotifications + 2}");
        feed[^1].Message.ShouldBe("note 3", "the three oldest went");
    }

    [Fact]
    public void AnEntryIsCountedUnreadUnlessTheFeedIsOnShowAndClearingEmptiesBoth()
    {
        var app = new GuiAppState();

        app.RecordNotification(Now, NotificationSeverity.Warning, "first");
        app.AppendNotification(Now, NotificationSeverity.Info, "second");
        app.UnreadNotificationCount.ShouldBe(2);
        app.StatusMessage.ShouldBe("second", "an appended one is the status line too");

        app.ActiveTab = GuiTab.Notifications;
        app.RecordNotification(Now, NotificationSeverity.Info, "read as it lands");
        app.UnreadNotificationCount.ShouldBe(2);
        app.Notifications.Select(static n => n.Message).ShouldBe(["read as it lands", "second", "first"]);

        app.ClearNotifications();
        app.Notifications.ShouldBeEmpty();
        app.UnreadNotificationCount.ShouldBe(0);
    }

    /// <summary>
    /// The defect itself, which only writers at the same moment show: four threads record a hundred entries each, and
    /// every one of the four hundred is in the feed. Read, insert and write back lost dozens of them. A thread stress
    /// test, so off CI and on threads of its own (<see cref="StressTestGate"/>): what CI runs is the swap's own two
    /// rules above, and <c>ImmutableInterlocked.Update</c> is the runtime's.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task EntriesRecordedFromSeveralThreadsAtOnceAreAllThere()
    {
        StressTestGate.SkipOnCi();
        const int Writers = 4;
        const int Each = 100; // four hundred in all, under the cap, so none is dropped as the oldest
        var ct = TestContext.Current.CancellationToken;
        var app = new GuiAppState();
        using var go = new ManualResetEventSlim();

        var writers = Enumerable.Range(0, Writers).Select(writer => Task.Factory.StartNew(() =>
        {
            go.Wait(ct);
            for (var i = 0; i < Each; i++)
            {
                app.RecordNotification(Now, NotificationSeverity.Info, $"{writer}:{i}");
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        go.Set();
        await Task.WhenAll(writers);

        app.Notifications.Select(static n => n.Message).Distinct().Count()
            .ShouldBe(Writers * Each, "an entry recorded while another was being recorded is not lost");
        app.UnreadNotificationCount.ShouldBe(Writers * Each, "and each is counted");
    }
}
