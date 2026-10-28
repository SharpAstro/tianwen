using System.Threading;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI's history of places (#1821): every tab switch is recorded by <see cref="GuiAppState.ActiveTab"/> itself, and
/// Alt+Left / Alt+Right and the mouse's back and forward buttons go through <see cref="GuiEventHandlerBase.HandleInput"/>
/// ahead of a focused field and the active tab.
/// </summary>
public class GuiBackForwardTests
{
    private sealed class Rig
    {
        public GuiAppState App { get; } = new();
        public PlannerState Planner { get; } = new();
        public SignalBus Bus { get; } = new();
        public BackgroundTaskTracker Tracker { get; } = new();
        public GuiEventHandlerBase Handler { get; }
        public IGuiChrome Chrome { get; }
        public IPixelWidget ActiveTabWidget { get; } = Substitute.For<IPixelWidget>();

        public Rig()
        {
            var sp = new ServiceCollection()
                .AddLogging()
                .AddSingleton<ITimeProvider>(new SystemTimeProvider())
                .AddSingleton(Substitute.For<ICelestialObjectDB>())
                .BuildServiceProvider();

            var chrome = Substitute.For<IGuiChrome>();
            chrome.Ui.Returns(new WindowUiSettings());
            chrome.Bus.Returns(Bus);
            chrome.SessionState.Returns(new SessionTabState());
            chrome.EquipmentState.Returns(new EquipmentTabState());
            chrome.ViewContexts.Returns(new ViewContexts());
            chrome.SkyMapState.Returns(new SkyMapState());
            chrome.ActiveTab.Returns(ActiveTabWidget);
            Chrome = chrome;

            var cts = new CancellationTokenSource();
            Handler = new GuiEventHandlerBase(sp, App, Planner, chrome, cts, cts.Token,
                Substitute.For<IExternal>(), Tracker);
        }

        public void Back() => Handler.HandleInput(new InputEvent.KeyDown(InputKey.Left, InputModifier.Alt)).ShouldBeTrue();

        public void Forward() => Handler.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.Alt)).ShouldBeTrue();
    }

    [Fact]
    public void ATabSwitchIsRecordedAndBackReturnsToTheTabItLeft()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        rig.App.ActiveTab = GuiTab.SkyMap;

        rig.Back();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.Back();
        rig.App.ActiveTab.ShouldBe(GuiTab.Home);

        rig.Forward();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.Forward();
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);
    }

    [Fact]
    public void SettingTheTabThatIsAlreadyOnScreenRecordsNothing()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Home;

        rig.App.History.CanGoBack.ShouldBeFalse();
    }

    [Fact]
    public void CtrlTabIsRecorded()
    {
        var rig = new Rig();

        rig.Handler.HandleInput(new InputEvent.KeyDown(InputKey.Tab, InputModifier.Ctrl)).ShouldBeTrue();
        rig.App.ActiveTab.ShouldBe(GuiAppState.NextTab(GuiTab.Home, forward: true));

        rig.Back();
        rig.App.ActiveTab.ShouldBe(GuiTab.Home);
    }

    [Fact]
    public void ACrossTabJumpThroughTheBusIsRecorded()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;

        rig.Bus.Post(new SelectLocalContextSignal(GuiTab.Session));
        rig.Bus.ProcessPending(rig.Tracker);
        rig.App.ActiveTab.ShouldBe(GuiTab.Session);

        rig.Back();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
    }

    [Fact]
    public void BackRestoresThePlannersSelectedTargetAndScrollsItIntoView()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        rig.Planner.SelectedTargetIndex = 7;
        rig.App.ActiveTab = GuiTab.SkyMap;
        rig.Planner.SelectedTargetIndex = 2; // moved on while away, as the sky map's "show in planner" does

        rig.Back();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.Planner.SelectedTargetIndex.ShouldBe(7);
        rig.Chrome.Received().PlannerEnsureVisible(7);

        rig.Forward();
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);
        rig.Planner.SelectedTargetIndex.ShouldBe(2, "forward comes back to the place Back left, selection included");
    }

    [Fact]
    public void ANewMoveAfterBackDropsTheForwardPlaces()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        rig.App.ActiveTab = GuiTab.SkyMap;
        rig.Back();
        rig.App.History.CanGoForward.ShouldBeTrue();

        rig.App.ActiveTab = GuiTab.Equipment;

        rig.App.History.CanGoForward.ShouldBeFalse();
        rig.Handler.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.Alt)).ShouldBeTrue("taken even with nowhere to go");
        rig.App.ActiveTab.ShouldBe(GuiTab.Equipment);
    }

    [Fact]
    public void ARestoreIsNotItselfRecorded()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        rig.App.ActiveTab = GuiTab.SkyMap;

        rig.Back();

        rig.App.History.BackCount.ShouldBe(1, "Home is the only place left behind; going back to Planner pushed nothing");
        rig.App.History.ForwardCount.ShouldBe(1);
    }

    [Fact]
    public void TheCanGoFlagsFollowBothEnds()
    {
        var rig = new Rig();
        rig.Handler.CanGoBack.ShouldBeFalse();
        rig.Handler.CanGoForward.ShouldBeFalse();

        rig.App.ActiveTab = GuiTab.Planner;
        rig.Handler.CanGoBack.ShouldBeTrue();
        rig.Handler.CanGoForward.ShouldBeFalse();

        rig.Back();
        rig.Handler.CanGoBack.ShouldBeFalse();
        rig.Handler.CanGoForward.ShouldBeTrue();

        rig.Handler.GoBack().ShouldBeFalse();
        rig.Forward();
        rig.Handler.CanGoForward.ShouldBeFalse();
        rig.Handler.GoForward().ShouldBeFalse();
    }

    [Fact]
    public void AltLeftAndRightGoAheadOfAFocusedTextField()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        var field = new TextInputState();
        rig.App.TextInputFocus.Focus(field, "M31");

        rig.Back();

        rig.App.ActiveTab.ShouldBe(GuiTab.Home);
        rig.Forward();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        field.Text.ShouldBe("M31", "the chord was Back, not the field's word-jump");
    }

    [Fact]
    public void TheMouseButtonsGoBackAndForwardAndTheirReleaseIsSwallowed()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;
        rig.App.ActiveTab = GuiTab.SkyMap;

        rig.Handler.HandleInput(new InputEvent.MouseDown(10, 10, MouseButton.Back)).ShouldBeTrue();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.Handler.HandleInput(new InputEvent.MouseUp(10, 10, MouseButton.Back)).ShouldBeTrue();
        rig.App.ActiveTab.ShouldBe(GuiTab.Planner, "the release does nothing");

        rig.Handler.HandleInput(new InputEvent.MouseDown(10, 10, MouseButton.Forward)).ShouldBeTrue();
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);
        rig.Handler.HandleInput(new InputEvent.MouseUp(10, 10, MouseButton.Forward)).ShouldBeTrue();
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);

        // Neither press nor release reached the active tab as a click.
        rig.ActiveTabWidget.DidNotReceiveWithAnyArgs().HandleInput(default!);
    }

    [Fact]
    public void ACorrectionIsNotAPlaceTheWindowWent()
    {
        var rig = new Rig();

        rig.App.SetActiveTabWithoutHistory(GuiTab.Equipment);

        rig.App.ActiveTab.ShouldBe(GuiTab.Equipment);
        rig.App.History.CanGoBack.ShouldBeFalse();
    }

    [Fact]
    public void AltWithAnotherModifierIsNotNavigation()
    {
        var rig = new Rig();
        rig.App.ActiveTab = GuiTab.Planner;

        rig.Handler.HandleInput(new InputEvent.KeyDown(InputKey.Left, InputModifier.Alt | InputModifier.Ctrl));

        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
    }
}
