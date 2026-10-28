using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
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
                .AddSingleton<LiveViewController>()
                .AddSingleton<ViewerState>()
                .AddSingleton<PlanetaryCaptureController>()
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

        /// <summary>
        /// Fills tonight's list with <paramref name="count"/> rows, as a planner that has scored a night shows them, so a
        /// selection is a real target and an index a real row (not a number into an empty list).
        /// </summary>
        public void SeedTargets(int count)
        {
            var scored = Enumerable.Range(0, count).Select(Scored).ToImmutableArray();
            Planner.TonightsBest = scored;
            Planner.ScoredTargets = scored.ToImmutableDictionary(s => s.Target);
        }

        public static Target TargetAt(int n) => new(1.0 + n, 20.0 + n, $"Test {n}", null);

        private static ScoredTarget Scored(int n) => new(
            TargetAt(n),
            (Half)1.0,
            (Half)1.0,
            new Dictionary<RaDecEventTime, RaDecEventInfo>(),
            OptimalStart: new DateTimeOffset(2026, 3, 20, 20, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(n),
            OptimalDuration: TimeSpan.FromHours(2),
            OptimalAltitude: 45.0,
            ObjectType: ObjectType.Galaxy);

        public Target SelectedTarget => PlannerActions.GetFilteredTargets(Planner)[Planner.SelectedTargetIndex].Target;

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
        rig.SeedTargets(10);
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
        rig.Planner.SelectedTargetIndex.ShouldBe(7, "a place on the atlas owns no planner selection, so Forward leaves the planner's cursor alone");
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

    /// <summary>
    /// The rail's strip (#1821): painted on a CPU surface, its Back cell is the region the router would press. A detail
    /// view opened from a list (the planner's Show in atlas) goes back to that list, its selection kept.
    /// </summary>
    [Fact]
    public void APressOnTheStripsBackRegionReturnsFromTheAtlasToThePlannersSelection()
    {
        var rig = new Rig();
        rig.SeedTargets(10);
        rig.App.ActiveTab = GuiTab.Planner;
        rig.Planner.SelectedTargetIndex = 7;

        rig.Bus.Post(new ViewInSkyMapSignal("M 31", 0.7123, 41.27, null, TianWen.Lib.Astrometry.Catalogs.ObjectType.Galaxy));
        rig.Bus.ProcessPending(rig.Tracker);
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);

        using var renderer = new RgbaImageRenderer(52, 28);
        var strip = new GuiNavigationStrip<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        // The handler bound itself to the chrome it was built over, which is how the real chrome hands it to its strip.
        strip.Navigation = rig.Chrome.Navigation.ShouldNotBeNull();
        strip.Render(new RectF32(0, 0, 52, GuiNavigationStrip<RgbaImage>.BaseHeight));

        var regions = strip.GetRegisteredRegions();
        var forward = regions.Single(r => r.Result is HitResult.ButtonHit { Action: Layout.Builder.NavigationForwardAction });
        var back = regions.Single(r => r.Result is HitResult.ButtonHit { Action: Layout.Builder.NavigationBackAction });
        back.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        rig.App.ActiveTab.ShouldBe(GuiTab.Planner, "the atlas was opened from the planner's list");
        rig.Planner.SelectedTargetIndex.ShouldBe(7);
        rig.Chrome.Received().PlannerEnsureVisible(7);

        // Its enabled state reads the history on the next frame: Back dimmed past Home is a swallowed press, Forward lit.
        strip.Render(new RectF32(0, 0, 52, GuiNavigationStrip<RgbaImage>.BaseHeight));
        rig.Handler.CanGoForward.ShouldBeTrue();
        forward = strip.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: Layout.Builder.NavigationForwardAction });
        forward.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);
    }

    /// <summary>
    /// A place keeps the planner's selected TARGET, not its row: the atlas pins the very target Show in atlas opened, the
    /// pinned section moves it to the top, and an index stored before would now point at another target.
    /// </summary>
    [Fact]
    public void BackSelectsTheSameTargetNotTheSameRowAfterThePlannersRowsMoved()
    {
        var rig = new Rig();
        rig.SeedTargets(5);
        rig.App.ActiveTab = GuiTab.Planner;
        rig.Planner.SelectedTargetIndex = 2;
        var opened = Rig.TargetAt(2);
        rig.SelectedTarget.ShouldBe(opened);

        rig.Bus.Post(new ViewInSkyMapSignal(opened.Name, opened.RA, opened.Dec, opened.CatalogIndex, ObjectType.Galaxy));
        rig.Bus.ProcessPending(rig.Tracker);
        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);

        rig.Bus.Post(new SkyMapPinObjectSignal(opened.Name, opened.RA, opened.Dec, opened.CatalogIndex, ObjectType.Galaxy));
        rig.Bus.ProcessPending(rig.Tracker);
        rig.Planner.Proposals.Length.ShouldBe(1, "the atlas pinned the target");
        PlannerActions.GetFilteredTargets(rig.Planner)[2].Target.ShouldNotBe(opened, "premise: row 2 is another target now");

        rig.Back();

        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.SelectedTarget.ShouldBe(opened);
        rig.Planner.SelectedTargetIndex.ShouldBe(0, "the pinned section comes first");
        rig.Chrome.Received().PlannerEnsureVisible(0);
    }

    [Fact]
    public void BackClampsTheCursorWhenTheSelectedTargetIsGone()
    {
        var rig = new Rig();
        rig.SeedTargets(10);
        rig.App.ActiveTab = GuiTab.Planner;
        rig.Planner.SelectedTargetIndex = 8;
        rig.App.ActiveTab = GuiTab.SkyMap;
        rig.SeedTargets(3); // a recompute while away dropped the target

        rig.Back();

        rig.App.ActiveTab.ShouldBe(GuiTab.Planner);
        rig.Planner.SelectedTargetIndex.ShouldBe(2);
    }

    [Fact]
    public void ABackToTheAtlasLeavesThePlannersSelectionAlone()
    {
        var rig = new Rig();
        rig.SeedTargets(10);
        rig.App.ActiveTab = GuiTab.SkyMap;
        rig.App.ActiveTab = GuiTab.Planner;
        rig.Planner.SelectedTargetIndex = 4;
        rig.App.ActiveTab = GuiTab.Equipment;

        rig.Back(); // to the planner, as the person left it
        rig.Planner.SelectedTargetIndex.ShouldBe(4);
        rig.Planner.SelectedTargetIndex = 6;
        rig.Back(); // to the atlas, which owns no selection

        rig.App.ActiveTab.ShouldBe(GuiTab.SkyMap);
        rig.Planner.SelectedTargetIndex.ShouldBe(6);
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
