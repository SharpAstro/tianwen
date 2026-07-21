using System;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    [Collection("Encoding")]
    public class StepExposureTests
    {
        [Theory]
        [InlineData(1, true, 2)]      // 1s steps below 10s
        [InlineData(5, true, 6)]
        [InlineData(9, true, 10)]     // crosses into 10s-step zone
        [InlineData(10, true, 20)]    // 10s steps below 60s
        [InlineData(50, true, 60)]
        [InlineData(60, true, 90)]    // 30s steps below 120s
        [InlineData(90, true, 120)]
        [InlineData(120, true, 180)]  // 60s steps above 120s
        [InlineData(180, true, 240)]
        [InlineData(3600, true, 3600)] // clamped at max
        public void StepExposure_Up(double fromSec, bool up, double expectedSec)
        {
            var result = SessionTabState.StepExposure(TimeSpan.FromSeconds(fromSec), up);
            result.TotalSeconds.ShouldBe(expectedSec);
        }

        [Theory]
        [InlineData(10, false, 9)]    // crosses back into 1s-step zone
        [InlineData(20, false, 10)]
        [InlineData(60, false, 50)]
        [InlineData(90, false, 60)]
        [InlineData(120, false, 90)]  // boundary: enters <120 zone, step=30 → 90
        [InlineData(180, false, 120)]
        [InlineData(2, false, 1)]
        [InlineData(1, false, 1)]     // clamped at min
        public void StepExposure_Down(double fromSec, bool up, double expectedSec)
        {
            var result = SessionTabState.StepExposure(TimeSpan.FromSeconds(fromSec), up);
            result.TotalSeconds.ShouldBe(expectedSec);
        }

        [Theory]
        [InlineData(121, true, 180)]   // snap to grid: ceil(121/60)*60=180
        [InlineData(121, false, 120)]  // snap to grid: floor(121/60)*60=120, 120<121 → 120
        [InlineData(45, true, 50)]     // ceil(45/10)*10=50, 50>45 → 50
        [InlineData(45, false, 40)]    // floor(45/10)*10=40, 40<45 → 40
        [InlineData(91, true, 120)]    // ceil(91/30)*30=120, 120>91 → 120
        [InlineData(91, false, 90)]    // floor(91/30)*30=90, 90<91 → 90
        [InlineData(5.5, true, 6)]     // ceil(5.5/1)*1=6
        [InlineData(5.5, false, 5)]    // floor(5.5/1)*1=5
        public void StepExposure_SnapsToGrid(double fromSec, bool up, double expectedSec)
        {
            var result = SessionTabState.StepExposure(TimeSpan.FromSeconds(fromSec), up);
            result.TotalSeconds.ShouldBe(expectedSec);
        }
    }

    [Collection("Encoding")]
    public class TryParseExposureInputTests
    {
        [Theory]
        [InlineData("120", 120)]
        [InlineData("30", 30)]
        [InlineData("1", 1)]
        [InlineData("3600", 3600)]
        [InlineData("90s", 90)]
        [InlineData("90S", 90)]
        [InlineData("2m", 120)]
        [InlineData("2M", 120)]
        [InlineData("1.5m", 90)]
        [InlineData("2min", 120)]
        [InlineData("2MIN", 120)]
        [InlineData("  60  ", 60)]
        public void ValidInput_ReturnsExpectedSeconds(string input, double expectedSec)
        {
            SessionTabState.TryParseExposureInput(input, out var result).ShouldBeTrue();
            result.TotalSeconds.ShouldBe(expectedSec);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-10")]
        [InlineData("3601")]    // over max
        [InlineData("0m")]
        [InlineData("61m")]     // 61 minutes > 3600s
        public void InvalidInput_ReturnsFalse(string input)
        {
            SessionTabState.TryParseExposureInput(input, out _).ShouldBeFalse();
        }
    }

    [Collection("UI")]
    public class SessionTabTests
    {
        private static SessionTab<RgbaImage> CreateTab(out SessionTabState state, int width = 800, int height = 600)
        {
            var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var tab = new SessionTab<RgbaImage>(renderer);
            state = tab.State;
            return tab;
        }

        private static void RenderTab(SessionTab<RgbaImage> tab, GuiAppState appState, PlannerState plannerState,
            float dpiScale = 1f)
        {
            var fontPath = ""; // empty font path — DrawText is a no-op but layout math still runs
            tab.Render(appState, plannerState, new RectF32(0, 0, 800, 600), dpiScale, fontPath);
        }

        [Fact]
        public void ClickOnConfigFieldLabel_SelectsField()
        {
            // Arrange
            var tab = CreateTab(out var state);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();

            // Render once to register clickable regions
            RenderTab(tab, appState, plannerState);

            // The config form should have registered fields
            state.FieldCount.ShouldBeGreaterThan(0);
            state.SelectedFieldIndex.ShouldBe(-1); // nothing selected initially

            // Act — click on the first field's label area (somewhere in the left part of the config panel)
            // Config panel fills the left side; first field is below the first group header
            var hit = tab.HitTestAndDispatch(10f, 60f); // approximate position of first field label

            // Assert
            hit.ShouldNotBeNull();
            hit.ShouldBeOfType<HitResult.ListItemHit>();
            var listHit = (HitResult.ListItemHit)hit;
            listHit.ListId.ShouldBe("ConfigField");
            state.SelectedFieldIndex.ShouldBe(listHit.Index);
        }

        [Fact]
        public void ClickOnConfigFieldLabel_WithGuiContentOffset_SelectsField()
        {
            // Arrange — simulate real GUI layout where content is offset by sidebar (52px) and status bar (28px)
            var tab = CreateTab(out var state, width: 1280, height: 900);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();

            // Render with content rect matching real GUI layout
            var contentRect = new RectF32(52f, 28f, 1280f - 52f, 900f - 28f);
            tab.Render(appState, plannerState, contentRect, 1f, "");

            state.FieldCount.ShouldBeGreaterThan(0);
            state.SelectedFieldIndex.ShouldBe(-1);

            // Act — click in the config label area (x=60 is 8px into the content, y=60 is 32px into content)
            // First group header is at y=28 (contentRect.Y), 28px tall, so first field starts at y≈56
            var hit = tab.HitTestAndDispatch(60f, 60f);

            // Assert
            hit.ShouldNotBeNull();
            hit.ShouldBeOfType<HitResult.ListItemHit>();
            state.SelectedFieldIndex.ShouldBeGreaterThanOrEqualTo(0);
        }

        [Fact]
        public void ClickOnStepperButton_ChangesConfig()
        {
            // Arrange
            var tab = CreateTab(out var state);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();
            RenderTab(tab, appState, plannerState);

            var initialConfig = state.Configuration;

            // Act — find and click a stepper button (Inc or Dec)
            // Stepper buttons are registered with action like "Inc:..." or "Dec:..."
            HitResult? buttonHit = null;
            // Scan across the first field's control area (right of label)
            for (var x = 170f; x < 350f; x += 5f)
            {
                var hit = tab.HitTest(x, 60f);
                if (hit is HitResult.ButtonHit { Action: var action } && (action.StartsWith("Inc:") || action.StartsWith("Dec:")))
                {
                    buttonHit = hit;
                    tab.HitTestAndDispatch(x, 60f); // fire the OnClick
                    break;
                }
            }

            // Assert
            buttonHit.ShouldNotBeNull("No stepper button found in the control area");
            state.Configuration.ShouldNotBe(initialConfig);
        }

        [Fact]
        public void WheelScroll_AccumulatesTrackpadDeltas_AndMirrorsAtomOffset()
        {
            // Arrange — short viewport so the config form overflows and the panel scrolls.
            var tab = CreateTab(out var state, width: 800, height: 200);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();
            var contentRect = new RectF32(0, 0, 800, 200);
            tab.Render(appState, plannerState, contentRect, 1f, "");

            state.ConfigScrollOffset.ShouldBe(0);

            // Act — one full wheel notch toward the end (negative delta): 3 atoms per notch.
            tab.HandleInput(new InputEvent.Scroll(-1f, 50f, 100f)).ShouldBeTrue();
            tab.Render(appState, plannerState, contentRect, 1f, "");

            // Assert — the state mirror carries the snapped canonical atom offset.
            state.ConfigScrollOffset.ShouldBe(3);

            // Act — ten sub-1.0 trackpad deltas: the fractional carry accumulates into a full notch
            // instead of truncating to zero (the bug the atom model exists to fix).
            for (var i = 0; i < 10; i++)
            {
                tab.HandleInput(new InputEvent.Scroll(-0.1f, 50f, 100f)).ShouldBeTrue();
            }
            tab.Render(appState, plannerState, contentRect, 1f, "");

            state.ConfigScrollOffset.ShouldBe(6);
        }

        [Fact]
        public void KeyboardDown_ScrollsSelectedFieldIntoView()
        {
            // Arrange — short viewport; walking the selection to the last field must scroll the panel.
            var tab = CreateTab(out var state, width: 800, height: 200);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();
            var contentRect = new RectF32(0, 0, 800, 200);
            tab.Render(appState, plannerState, contentRect, 1f, "");

            state.FieldCount.ShouldBeGreaterThan(1);
            state.SelectedFieldIndex = 0;

            // Act — walk the selection to the bottom of the form.
            for (var i = 0; i < state.FieldCount - 1; i++)
            {
                tab.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeTrue();
            }
            tab.Render(appState, plannerState, contentRect, 1f, "");

            // Assert — EnsureFieldVisible drove the controller past the top.
            state.SelectedFieldIndex.ShouldBe(state.FieldCount - 1);
            state.ConfigScrollOffset.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void KeyboardUpDown_NavigatesFields()
        {
            // Arrange
            var tab = CreateTab(out var state);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();
            RenderTab(tab, appState, plannerState);

            state.SelectedFieldIndex = 0;

            // Act — press Down
            tab.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeTrue();
            state.SelectedFieldIndex.ShouldBe(1);

            // Act — press Up
            tab.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeTrue();
            state.SelectedFieldIndex.ShouldBe(0);

            // Act — press Up at top (should stay at 0)
            tab.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeTrue();
            state.SelectedFieldIndex.ShouldBe(0);
        }

        [Fact]
        public void KeyboardLeftRight_AdjustsSelectedField()
        {
            // Arrange
            var tab = CreateTab(out var state);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();
            RenderTab(tab, appState, plannerState);

            state.SelectedFieldIndex = 0;
            var initialConfig = state.Configuration;

            // Act — press Right to increment
            tab.HandleInput(new InputEvent.KeyDown(InputKey.Right)).ShouldBeTrue();

            // Assert — config should have changed
            state.Configuration.ShouldNotBe(initialConfig);
        }

        [Fact]
        public void FieldCount_MatchesConfigGroups()
        {
            // Arrange
            var tab = CreateTab(out var state);
            var appState = new GuiAppState();
            var plannerState = new PlannerState();

            // Act
            RenderTab(tab, appState, plannerState);

            // Assert — should match total fields across all groups
            var expectedCount = 0;
            foreach (var group in SessionConfigGroups.Groups)
            {
                expectedCount += group.Fields.Length;
            }

            state.FieldCount.ShouldBe(expectedCount);
        }
    }

    /// <summary>
    /// Regression guard for "the session captured 120s, not the 45s shown in the config screen".
    /// The Session config displays the per-OTA f-ratio default exposure, but schedule building
    /// used to pass a hardcoded 120s -- so what the user saw on the target rows was NOT what the
    /// session captured. <see cref="SessionContent.EffectiveDefaultSubExposure"/> is now the
    /// single source of truth both the display and the scheduler read.
    /// </summary>
    [Collection("UI")]
    public class EffectiveDefaultSubExposureTests
    {
        // f/3 OTA (FL 300mm / aperture 100mm) -> DefaultExposureFromFRatio = 5*3*3 = 45s,
        // i.e. exactly the "45s in the config" the user saw -- and crucially NOT 120s.
        private static SessionTabState StateWithF3Ota()
        {
            var state = new SessionTabState();
            state.CameraSettings.Add(new PerOtaCameraSettings
            {
                OtaName = "Telescope #0",
                FocalLength = 300,
                Aperture = 100,
            });
            return state;
        }

        [Fact]
        public void WhenNoConfigOverride_EffectiveDefaultEqualsDisplayedFRatioDefaultNot120()
        {
            var state = StateWithF3Ota();
            state.Configuration = state.Configuration with { DefaultSubExposure = null };

            // What the config screen displays for an unoverridden target:
            var displayedSec = SessionContent.DefaultExposureSeconds(state);
            displayedSec.ShouldBe(45); // f/3 -> 45s shown; NOT the old hardcoded 120

            // The scheduler's default MUST equal what the screen shows (the broken invariant).
            SessionContent.EffectiveDefaultSubExposure(state).ShouldBe(TimeSpan.FromSeconds(displayedSec));
            SessionContent.EffectiveDefaultSubExposure(state).ShouldNotBe(TimeSpan.FromSeconds(120));
        }

        [Fact]
        public void WhenConfigOverrideSet_EffectiveDefaultHonoursConfigValue()
        {
            var state = StateWithF3Ota();
            state.Configuration = state.Configuration with { DefaultSubExposure = TimeSpan.FromSeconds(90) };

            SessionContent.EffectiveDefaultSubExposure(state).ShouldBe(TimeSpan.FromSeconds(90));
        }
    }
}
