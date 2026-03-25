using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
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
}
