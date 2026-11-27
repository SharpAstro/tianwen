using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #2507: the Notifications tab's desktop alert choice. The tab is rendered on the CPU renderer, a click on the choice's
/// painted region goes through the <see cref="InputRouter"/> as a real press does, and the level and the save are read
/// back. In memory, against a fake sink.
/// </summary>
public class NotificationsTabAlertSettingTests(ITestOutputHelper output)
{
    internal sealed class Sink : IDesktopAlert
    {
        public bool IsAvailable { get; set; } = true;

        public string? Refusal { get; set; }

        public DesktopAlertResult Show(DesktopAlert alert) => Refusal is { } why ? DesktopAlertResult.Unavailable(why) : DesktopAlertResult.Ok;
    }

    private static DesktopAlerts AlertsOn(Sink sink)
        => new(sink, new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero)), () => false);

    private sealed record Painted(NotificationsTab<RgbaImage> Tab, SignalBus Bus, List<DesktopAlertLevel> Chosen);

    private static Painted Paint(RgbaImageRenderer renderer, GuiAppState app, ViewContext view, float dpiScale = 1f)
    {
        var bus = new SignalBus();
        var chosen = new List<DesktopAlertLevel>();
        bus.Subscribe<SetDesktopAlertLevelSignal>(s => chosen.Add(s.Level));
        var tab = new NotificationsTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
        tab.Render(app, view, new RectF32(0, 0, renderer.Width, renderer.Height));
        return new Painted(tab, bus, chosen);
    }

    private static ClickableRegion[] ChoiceRegions(NotificationsTab<RgbaImage> tab)
        => [.. tab.GetRegisteredRegions().Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("DesktopAlert"))];

    private static ClickableRegion Region(NotificationsTab<RgbaImage> tab, string action)
        => ChoiceRegions(tab).Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);

    private static void Click(Painted painted, ClickableRegion region)
    {
        var (x, y) = (region.X + region.Width / 2f, region.Y + region.Height / 2f);
        var router = new InputRouter(painted.Tab.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [painted.Tab] };
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        painted.Bus.ProcessPending();
    }

    [Theory]
    [InlineData("DesktopAlertOff", DesktopAlertLevel.Off, 1f)]
    [InlineData("DesktopAlertAll", DesktopAlertLevel.AllWarnings, 1f)]
    [InlineData("DesktopAlertOff", DesktopAlertLevel.Off, 1.5f)]
    [InlineData("DesktopAlertAll", DesktopAlertLevel.AllWarnings, 1.5f)]
    public void AClickOnASegmentPostsItsLevel(string action, DesktopAlertLevel expected, float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(1200, 600);
        var app = new GuiAppState { DesktopAlerts = AlertsOn(new Sink()) };
        var painted = Paint(renderer, app, new ViewContexts().Local, dpiScale);

        Click(painted, Region(painted.Tab, action));

        painted.Chosen.ShouldBe([expected]);
    }

    // The header's geometry from the painted regions: everything inside the header, the three segments equal and abutting,
    // Clear and the choice on one middle, and, where the width cannot hold it all on one row, the choice on a second row.
    [Theory]
    [InlineData(1000, 1f, false)]
    [InlineData(1000, 1.5f, false)]
    [InlineData(560, 1f, false)]
    [InlineData(560, 1.5f, true)]
    public void TheHeaderLaysOutBesideOrBelowClearInsideItsRect(int width, float dpiScale, bool wraps)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 260u);
        var app = new GuiAppState { DesktopAlerts = AlertsOn(new Sink()) };
        app.RecordNotification(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero), NotificationSeverity.Error, "A failure");
        var tab = Paint(renderer, app, new ViewContexts().Local, dpiScale).Tab;

        var segments = ChoiceRegions(tab).OrderBy(r => r.X).ToArray();
        var clear = tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "NotificationsClear" });
        var oneRow = 28f * dpiScale;
        var inHeader = (float bottom) => bottom <= oneRow * (wraps ? 2f : 1f) + 0.5f;

        segments.Length.ShouldBe(3);
        foreach (var r in segments.Append(clear))
        {
            r.X.ShouldBeGreaterThanOrEqualTo(0f);
            (r.X + r.Width).ShouldBeLessThanOrEqualTo(width + 0.5f);
            r.Y.ShouldBeGreaterThanOrEqualTo(0f);
            inHeader(r.Y + r.Height).ShouldBeTrue();
        }
        segments[1].Width.ShouldBe(segments[0].Width, 1f);
        segments[2].Width.ShouldBe(segments[0].Width, 1f);
        (segments[1].X - (segments[0].X + segments[0].Width)).ShouldBeLessThanOrEqualTo(2f * dpiScale);
        (segments[2].X - (segments[1].X + segments[1].Width)).ShouldBeLessThanOrEqualTo(2f * dpiScale);

        var choiceMiddle = segments[0].Y + segments[0].Height / 2f;
        var clearMiddle = clear.Y + clear.Height / 2f;
        if (wraps)
        {
            choiceMiddle.ShouldBeGreaterThan(clearMiddle + oneRow / 2f);
        }
        else
        {
            choiceMiddle.ShouldBe(clearMiddle, 1f);
            segments[0].Height.ShouldBe(clear.Height, 1f);
            clear.X.ShouldBeGreaterThan(segments[2].X + segments[2].Width - 1f);
        }
    }

    [Fact]
    public void TheChoiceIsDrawnBesideClearOnThisComputersFeed()
    {
        using var renderer = new RgbaImageRenderer(1200, 600);
        var app = new GuiAppState { DesktopAlerts = AlertsOn(new Sink()) };
        app.RecordNotification(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero), NotificationSeverity.Error, "A failure");

        var tab = Paint(renderer, app, new ViewContexts().Local).Tab;

        ChoiceRegions(tab).Length.ShouldBe(3);
        tab.GetRegisteredRegions().Any(r => r.Result is HitResult.ButtonHit { Action: "NotificationsClear" }).ShouldBeTrue();
    }

    [Fact]
    public void WithAlertsUnavailableOrAbsentTheChoiceIsNotDrawn()
    {
        using var renderer = new RgbaImageRenderer(1200, 600);

        ChoiceRegions(Paint(renderer, new GuiAppState { DesktopAlerts = AlertsOn(new Sink { IsAvailable = false }) },
            new ViewContexts().Local).Tab).ShouldBeEmpty();
        ChoiceRegions(Paint(renderer, new GuiAppState(), new ViewContexts().Local).Tab).ShouldBeEmpty();
    }

    // Kept output only (TIANWEN_KEEP_TEST_OUTPUT=1): the tab beside the test binary, narrow and wide, at both scales, to be looked at.
    [Theory]
    [InlineData(560, 1f)]
    [InlineData(560, 1.5f)]
    [InlineData(1000, 1f)]
    [InlineData(1000, 1.5f)]
    public void TheHeaderRendersWithTheChoiceAtBothScales(int width, float dpiScale)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 260u);
        var app = new GuiAppState { DesktopAlerts = AlertsOn(new Sink { Refusal = "Disabled: toasts are off for this app" }) };
        var now = new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero);
        app.RecordNotification(now, NotificationSeverity.Error, "Mount://ASCOMDevice/ASCOM.OnStep.Telescope refused the slew to Sharpless 2-132");
        app.RecordNotification(now, NotificationSeverity.Warning, "Guiding RMS 0.9 arcsec");
        app.RecordNotification(now, NotificationSeverity.Info, "Session started");
        var bus = new SignalBus();
        var tab = new NotificationsTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };

        tab.Render(app, new ViewContexts().Local, new RectF32(0, 0, renderer.Width, renderer.Height));

        ChoiceRegions(tab).Length.ShouldBe(3);
        if (Environment.GetEnvironmentVariable("TIANWEN_KEEP_TEST_OUTPUT") == "1")
        {
            System.IO.File.WriteAllBytes(
                System.IO.Path.Combine(AppContext.BaseDirectory, $"notifications-alerts-{width}-{dpiScale:0.0}.png"),
                PngWriter.Encode(renderer.Surface.Pixels, renderer.Surface.Width, renderer.Surface.Height));
        }
    }

    [Fact]
    public async Task AChoiceChangesTheLevelAndIsSavedForTheNextStart()
    {
        var external = new FakeExternal(output);
        var alerts = AlertsOn(new Sink());

        await DesktopAlertSettings.ChooseAsync(alerts, DesktopAlertLevel.AllWarnings, external, TestContext.Current.CancellationToken);

        alerts.Level.ShouldBe(DesktopAlertLevel.AllWarnings);
        (await DesktopAlertSettings.LoadAsync(external, TestContext.Current.CancellationToken)).ShouldBe(DesktopAlertLevel.AllWarnings);
    }

    [Fact]
    public async Task TheTooltipSaysWhyWindowsRefusedTheLastAlert()
    {
        var alerts = AlertsOn(new Sink { Refusal = "Disabled: toasts are off for this app" });
        NotificationsTab<RgbaImage>.AlertTooltip(alerts).ShouldNotContain("did not show");

        alerts.Offer(new NotificationEntry(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero), NotificationSeverity.Error, "A failure", NoteKind.None), null, null);
        alerts.Complete();
        await alerts.RunAsync(TestContext.Current.CancellationToken);

        NotificationsTab<RgbaImage>.AlertTooltip(alerts).ShouldContain("toasts are off for this app");
    }
}
