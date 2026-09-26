using System;
using System.Collections.Generic;
using Console.Lib;
using DIR.Lib;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Cli.Tui;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A TUI tab rebuilds its lists on every <see cref="TuiTabBase.Attach"/>, which runs on each tab switch
/// (and resize). The cursor is the user's place in the list, so it has to come back: before
/// <c>TuiTabBase.HostList</c> it reset to row 0, and switching away from the Equipment tab and back lost
/// the selected <c>Mount</c> row (issue #599).
/// </summary>
public class TuiListCursorTests
{
    private static IVirtualTerminal Terminal()
    {
        var terminal = Substitute.For<IVirtualTerminal>();
        terminal.Size.Returns((80, 40));
        return terminal;
    }

    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero);

    private static void Press(TuiTabBase tab, InputKey key, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            tab.HandleInput(new InputEvent.KeyDown(key, InputModifier.None));
        }
    }

    /// <summary>What the TUI loop does on a switch: the other tab is attached, then this one again.</summary>
    private static void SwitchAwayAndBack(TuiTabBase tab, TuiTabBase other, IVirtualTerminal terminal)
    {
        other.Attach(terminal);
        other.Render();
        tab.Attach(terminal);
        tab.Render();
    }

    private static TuiEquipmentTab EquipmentTab(GuiAppState appState)
    {
        var host = Substitute.For<IConsoleHost>();
        host.ListDevicesAsync<Profile>(default, default, default)
            .ReturnsForAnyArgs(System.Threading.Tasks.Task.FromResult<IReadOnlyCollection<Profile>>([]));

        return new TuiEquipmentTab(appState, new EquipmentTabState(), new ViewContexts(), new EquipmentContent(), host);
    }

    private static GuiAppState WithProfile()
    {
        var none = NoneDevice.Instance.DeviceUri;
        var appState = new GuiAppState();
        appState.ActiveProfile = new Profile(Guid.NewGuid(), "Rig", new ProfileData(
            Mount: none, Guider: none,
            OTAs: [new OTAData("OTA 1", 1000, Camera: none, Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null)]));
        return appState;
    }

    [Fact]
    public void TheEquipmentSettingsRowSurvivesATabSwitch()
    {
        var terminal = Terminal();
        var appState = WithProfile();
        var tab = EquipmentTab(appState);
        var other = new TuiNotificationsTab(appState);

        tab.Attach(terminal);
        tab.Render();
        // Row 0 is the "Profile Devices" header, so the first build lands on row 1; two Downs reach row 3.
        tab.ListCursorIndex(TuiEquipmentTab.SettingsKey).ShouldBe(1);
        Press(tab, InputKey.Down, 2);
        tab.Render();
        tab.ListCursorIndex(TuiEquipmentTab.SettingsKey).ShouldBe(3);

        SwitchAwayAndBack(tab, other, terminal);

        tab.ListCursorIndex(TuiEquipmentTab.SettingsKey).ShouldBe(3);
    }

    [Fact]
    public void TheNotificationRowSurvivesATabSwitch()
    {
        var terminal = Terminal();
        var appState = new GuiAppState();
        for (var i = 0; i < 6; i++)
        {
            appState.AppendNotification(Now.AddMinutes(i), NotificationSeverity.Info, $"note {i}");
        }

        var tab = new TuiNotificationsTab(appState);
        tab.Attach(terminal);
        tab.Render();
        Press(tab, InputKey.Down, 3);
        tab.Render();
        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(3);

        SwitchAwayAndBack(tab, EquipmentTab(appState), terminal);

        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(3);
    }

    /// <summary>A resize re-attaches without a render in between; the remembered row must outlive that.</summary>
    [Fact]
    public void TwoAttachesBeforeARenderKeepTheRow()
    {
        var terminal = Terminal();
        var appState = new GuiAppState();
        for (var i = 0; i < 6; i++)
        {
            appState.AppendNotification(Now.AddMinutes(i), NotificationSeverity.Info, $"note {i}");
        }

        var tab = new TuiNotificationsTab(appState);
        tab.Attach(terminal);
        tab.Render();
        Press(tab, InputKey.Down, 3);

        tab.Attach(terminal);
        tab.Attach(terminal);
        tab.Render();

        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(3);
    }

    [Fact]
    public void TheRowIsClampedWhenTheRebuiltListIsShorter()
    {
        var terminal = Terminal();
        var appState = new GuiAppState();
        for (var i = 0; i < 6; i++)
        {
            appState.AppendNotification(Now.AddMinutes(i), NotificationSeverity.Info, $"note {i}");
        }

        var tab = new TuiNotificationsTab(appState);
        tab.Attach(terminal);
        tab.Render();
        Press(tab, InputKey.Down, 5);
        tab.Render();
        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(5);

        var other = EquipmentTab(appState);
        other.Attach(terminal);
        other.Render();

        // While away, the list shrinks to three rows.
        appState.ClearNotifications();
        for (var i = 0; i < 3; i++)
        {
            appState.AppendNotification(Now.AddMinutes(10 + i), NotificationSeverity.Info, $"later {i}");
        }

        tab.Attach(terminal);
        tab.Render();

        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(2);
    }

    [Fact]
    public void AnEmptyRebuiltListStaysAtRowZero()
    {
        var terminal = Terminal();
        var appState = new GuiAppState();
        for (var i = 0; i < 6; i++)
        {
            appState.AppendNotification(Now.AddMinutes(i), NotificationSeverity.Info, $"note {i}");
        }

        var tab = new TuiNotificationsTab(appState);
        tab.Attach(terminal);
        tab.Render();
        Press(tab, InputKey.Down, 4);

        appState.ClearNotifications();
        tab.Attach(terminal);
        tab.Render();

        // Nothing to select, so the restore leaves the list exactly as a never-touched empty one is.
        var fresh = new TuiNotificationsTab(appState);
        fresh.Attach(terminal);
        fresh.Render();
        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(fresh.ListCursorIndex(TuiNotificationsTab.ListKey));

        // And the row is not held over to reappear once the list refills.
        appState.AppendNotification(Now.AddMinutes(20), NotificationSeverity.Info, "again");
        tab.Render();
        tab.ListCursorIndex(TuiNotificationsTab.ListKey).ShouldBe(0);
    }
}
