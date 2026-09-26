using System;
using System.Threading.Tasks;
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
/// The TUI Equipment tab's device-slot row draws <c>[On|Off]</c> and <c>[&gt;]</c> on every row, and until #598
/// neither was bound: a click did nothing, so connecting a device or opening the picker was keyboard-only. Each
/// case here runs twice, once by KEY and once by CLICK, through the real tab over the GUI's real signal handler,
/// and asserts the same outcome -- so a click that took a different route from the key (and could therefore skip
/// the ownership gate, the disconnect safety pre-check or the confirm strip) would show up as a different result.
/// </summary>
public class TuiEquipmentSlotClickTests(ITestOutputHelper output)
{
    private const int Columns = 120;
    private const int Rows = 40;

    /// <summary>The real tab, rendered once over a terminal with 1x1-pixel cells so a mouse point IS a cell.</summary>
    private static TuiEquipmentTab OpenTab(GuiSignalHarness h)
    {
        var terminal = Substitute.For<IVirtualTerminal>();
        terminal.Size.Returns((Columns, Rows));
        terminal.CellSize.Returns(new TermCell(1, 1));

        var tab = new TuiEquipmentTab(h.AppState, h.Equipment, h.Contexts, new EquipmentContent(h.Hub),
            Substitute.For<IConsoleHost>(), h.Bus);
        tab.Attach(terminal);
        tab.Render();
        return tab;
    }

    /// <summary>The screen row the slot for <paramref name="deviceUri"/> was painted on, and the row's column span.</summary>
    private static (int Y, int Left, int Right, EquipmentFieldItem Item) FindSlotRow(TuiEquipmentTab tab, Uri deviceUri)
    {
        var list = tab.SettingsList.ShouldNotBeNull();
        for (var y = 0; y < Rows; y++)
        {
            for (var x = 0; x < Columns; x++)
            {
                if (list.HitTestRow(x, y) is { Item: { SlotDeviceUri: { } uri } item } && DeviceBase.SameDevice(uri, deviceUri))
                {
                    var right = x;
                    while (right + 1 < Columns && list.HitTestRow(right + 1, y) is not null)
                    {
                        right++;
                    }
                    return (y, x, right, item);
                }
            }
        }
        throw new ShouldAssertException($"no slot row was painted for {deviceUri}");
    }

    /// <summary>
    /// The cell a click on <paramref name="label"/> lands on, taken from the row's own tree arranged over the
    /// span the list painted it into. Looked up by the drawn TEXT, not by a hit, so it finds the glyph whether or
    /// not it is bound -- which is what lets the same test run against the unbound row.
    /// </summary>
    private static (int X, int Y) CellOf(TuiEquipmentTab tab, Uri deviceUri, string label)
    {
        var (y, left, right, item) = FindSlotRow(tab, deviceUri);
        var arranged = Layout.Engine.Arrange(
            item.BuildRow(RowContext.Single(selected: false)),
            new Rect<int>(left, y, right - left + 1, 1),
            CellMeasureContext.CellAuthored);

        foreach (var node in arranged)
        {
            if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var value } } && value == label)
            {
                return (node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y);
            }
        }
        throw new ShouldAssertException($"the row for {deviceUri} draws no '{label}'");
    }

    /// <summary>A left click as the TUI delivers one: the press moves the cursor, the release dispatches.</summary>
    private static void Click(TuiEquipmentTab tab, int x, int y)
    {
        tab.HandleInput(new InputEvent.MouseDown(x, y, MouseButton.Left));
        tab.HandleInput(new InputEvent.MouseUp(x, y, MouseButton.Left));
    }

    private static void Press(TuiEquipmentTab tab, InputKey key)
        => tab.HandleInput(new InputEvent.KeyDown(key, InputModifier.None));

    /// <summary>
    /// The Mount is the first slot, which is where the cursor lands on the first build, so its KEY run needs no
    /// navigation; a device deeper in the list is reached by walking the cursor down to it.
    /// </summary>
    private static void SelectRow(TuiEquipmentTab tab, Uri deviceUri)
    {
        var list = tab.SettingsList.ShouldNotBeNull();
        for (var guard = 0; guard < 64; guard++)
        {
            if (list.Selected is { SlotDeviceUri: { } uri } && DeviceBase.SameDevice(uri, deviceUri))
            {
                return;
            }
            Press(tab, InputKey.Down);
            tab.Render();
        }
        throw new ShouldAssertException($"the cursor never reached the row for {deviceUri}");
    }

    /// <summary>Toggles the slot's connection the way <paramref name="byClick"/> says: its Off segment, or O.</summary>
    private static void TurnOff(GuiSignalHarness h, TuiEquipmentTab tab, Uri deviceUri, bool byClick)
    {
        if (byClick)
        {
            var (x, y) = CellOf(tab, deviceUri, "Off");
            Click(tab, x, y);
        }
        else
        {
            SelectRow(tab, deviceUri);
            Press(tab, InputKey.O);
        }
        h.Bus.ProcessPending(h.Tracker);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffDisconnectsAnUnclaimedDevice(bool byClick)
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var tab = OpenTab(h);

        TurnOff(h, tab, h.MountUri, byClick);

        await h.UntilAsync(() => !h.Hub.IsConnected(h.MountUri), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A device a run has leased is refused, whichever way the user asks: the click reaches the same ownership
    /// gate as the key, and the user is told who holds it rather than being offered the warm/force strip.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffOnALeasedDeviceIsRefused(bool byClick)
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        h.Hub.TryAcquireLease(h.MountUri, "the imaging session", out var lease).ShouldBeTrue();
        using var _ = lease;
        var tab = OpenTab(h);

        TurnOff(h, tab, h.MountUri, byClick);

        h.ShouldHaveRefused("the imaging session");
        h.Hub.IsConnected(h.MountUri).ShouldBeTrue("a leased device was disconnected");
        h.Equipment.PendingDisconnectConfirm.ShouldBeNull("a leased device was offered the warm/force strip");
    }

    /// <summary>
    /// A cooled camera is held by the safety pre-check and raises the confirm strip, by click as by key -- the
    /// click must not disconnect a cold sensor outright.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffOnACooledCameraRaisesTheConfirmStrip(bool byClick)
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        h.Hub.TryGetConnectedDriver<ICameraDriver>(h.CameraUri, out var camera).ShouldBeTrue();
        await camera.ShouldNotBeNull().SetCoolerOnAsync(true, TestContext.Current.CancellationToken);
        var tab = OpenTab(h);

        TurnOff(h, tab, h.CameraUri, byClick);

        await h.UntilAsync(() => h.Equipment.PendingDisconnectConfirm is not null, TestContext.Current.CancellationToken);
        DeviceBase.SameDevice(h.Equipment.PendingDisconnectConfirm, h.CameraUri).ShouldBeTrue();
        h.Hub.IsConnected(h.CameraUri).ShouldBeTrue("a cooled camera was disconnected without the confirm strip");
    }

    /// <summary><c>[&gt;]</c> opens the same assignment picker as Enter, for the row it was drawn on.</summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePickerGlyphOpensTheAssignmentPicker(bool byClick)
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var tab = OpenTab(h);
        var expected = FindSlotRow(tab, h.CameraUri).Item.Slot.ShouldNotBeNull();

        if (byClick)
        {
            var (x, y) = CellOf(tab, h.CameraUri, EquipmentFieldItem.PickerActionLabel);
            Click(tab, x, y);
        }
        else
        {
            SelectRow(tab, h.CameraUri);
            Press(tab, InputKey.Enter);
        }

        h.Equipment.ActiveAssignment.ShouldBe(expected);
    }
}
