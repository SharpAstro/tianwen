using System;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// #2025: a warning or an error is told from an accented control by FORM as well as colour. The Notifications row
    /// carries a severity mark (a baked triangle or cross, declared as a keyed <see cref="Layout.Content.Fill"/>) beside
    /// its stripe for a Warning and an Error, none for Info, and the slot is reserved either way so the timestamp column
    /// never moves with a row's severity.
    /// </summary>
    public class NotificationSeverityMarkTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 6, 21, 0, 0, TimeSpan.Zero);

        private static Layout.ArrangedNode<int>[] Arranged(NotificationSeverity severity)
        {
            var row = NotificationsTab<RgbaImage>.BuildRow(new NotificationEntry(Now, severity, "Something happened"), "21:00:00", alternate: false);
            return Layout.Engine.Arrange(row, new Rect<int>(0, 0, 800, 32), CellMeasureContext.PixelAuthored).ToArray();
        }

        private static string[] MarkKeys(Layout.ArrangedNode<int>[] arranged) => arranged
            .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } key } } ? key : null)
            .OfType<string>()
            .Where(k => k.StartsWith(SeverityMarks.KeyPrefix, StringComparison.Ordinal))
            .ToArray();

        [Theory]
        [InlineData(NotificationSeverity.Warning)]
        [InlineData(NotificationSeverity.Error)]
        public void AWarningAndAnErrorRowCarryASeverityMark(NotificationSeverity severity)
        {
            var key = MarkKeys(Arranged(severity)).ShouldHaveSingleItem();

            key.ShouldContain(severity.ToString());
            // The mark resolves back to a baked mask in the palette's ink for that severity.
            SeverityMarks.TryResolve(key, 13f, out _, out var ink).ShouldBeTrue();
            ink.ShouldBe(SeverityMarks.InkOf(severity));
        }

        [Fact]
        public void AnInfoRowCarriesNoSeverityMark()
        {
            MarkKeys(Arranged(NotificationSeverity.Info)).ShouldBeEmpty();
        }

        [Fact]
        public void TheTimestampColumnDoesNotMoveWithTheSeverity()
        {
            // The slot is reserved for Info rows (an empty spacer), so every row's timestamp starts at the same x.
            static int TimestampX(Layout.ArrangedNode<int>[] arranged) => arranged
                .Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "21:00:00" } }).Bounds.X;

            var info = TimestampX(Arranged(NotificationSeverity.Info));
            TimestampX(Arranged(NotificationSeverity.Warning)).ShouldBe(info);
            TimestampX(Arranged(NotificationSeverity.Error)).ShouldBe(info);
        }
    }
}
