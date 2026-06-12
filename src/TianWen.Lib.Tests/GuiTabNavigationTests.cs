using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tab-cycling logic behind Ctrl+Tab / Ctrl+Shift+Tab. The order itself lives in
/// <see cref="GuiAppState.TabOrder"/> (single source of truth shared with the GUI sidebar).
/// </summary>
public class GuiTabNavigationTests
{
    [Fact]
    public void TabOrder_IsTheSidebarLayoutOrder()
    {
        GuiAppState.TabOrder.ShouldBe(new[]
        {
            GuiTab.Equipment,
            GuiTab.Planner,
            GuiTab.SkyMap,
            GuiTab.Session,
            GuiTab.LiveSession,
            GuiTab.Guider,
            GuiTab.Notifications,
        });
    }

    [Fact]
    public void NextTab_Forward_AdvancesOneStepAndWrapsAtEnd()
    {
        var order = GuiAppState.TabOrder;
        for (var i = 0; i < order.Length; i++)
        {
            var expected = order[(i + 1) % order.Length];
            GuiAppState.NextTab(order[i], forward: true).ShouldBe(expected);
        }
    }

    [Fact]
    public void NextTab_Backward_StepsBackOneAndWrapsAtStart()
    {
        var order = GuiAppState.TabOrder;
        for (var i = 0; i < order.Length; i++)
        {
            var expected = order[(i - 1 + order.Length) % order.Length];
            GuiAppState.NextTab(order[i], forward: false).ShouldBe(expected);
        }
    }

    [Fact]
    public void NextTab_ForwardThenBackward_ReturnsToStartForEveryTab()
    {
        foreach (var tab in GuiAppState.TabOrder)
        {
            GuiAppState.NextTab(GuiAppState.NextTab(tab, forward: true), forward: false)
                .ShouldBe(tab);
        }
    }
}
