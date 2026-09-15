using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The contract the GUI chrome reads the navigation rail through: a rail cell is one painted node
/// carrying <c>ListItemHit(TabBarRegions.Tabs, index)</c>, a LOCKED one carries
/// <c>TabBarRegions.DisabledTabs</c> instead, and the index is the position in the item list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TabBar{TSurface}"/> is generic over its SURFACE, not over the item type, so it reports a
/// cell as an indexed hit and cannot know what an index stands for. <c>VkGuiRenderer.RailTab</c> turns
/// that index into a <see cref="GuiTab"/> and re-states each cell as the button that selects it and the
/// chord that reaches it -- one handler and one <see cref="KeyChord"/> per cell, in
/// <c>CollectPaintedRegions</c> and <c>CollectPaintedNodes</c>. None of that is testable here (the
/// chrome is Vulkan-only), but ALL of it reads the shape below, so this is where a change in the bar
/// would surface as something other than a rail that silently stops answering.
/// </para>
/// <para>
/// The disabled case is the one worth pinning twice over: it is what makes a locked tab's chord inert
/// without a guard beside the key, the router matching a shortcut only against nodes the paint drew and
/// <c>RailTab</c> declining anything that is not an ENABLED cell.
/// </para>
/// </remarks>
[Collection("UI")]
public class SidebarTabShortcutTests
{
    private const float RailWidth = 52f;

    private static (RgbaImageRenderer Renderer, TabBar<RgbaImage> Bar) Build()
    {
        var renderer = new RgbaImageRenderer(400, 600);
        var bar = new TabBar<RgbaImage>(renderer)
        {
            FontPath = FontResolver.ResolveSystemFont(),
            Side = TabStripSide.Left,
            Sizing = TabSizing.Uniform,
            CanCloseTabs = false,
            CanReorderTabs = false,
        };
        return (renderer, bar);
    }

    [Fact]
    public void EveryRailCellIsOneNodeCarryingItsIndexedHit()
    {
        var (renderer, bar) = Build();
        using var _ = renderer;

        var items = GuiAppState.TabOrder
            .Select(tab => new TabItem<GuiTab>(tab.ToString(), tab) { Icon = "#" })
            .ToArray();
        bar.Render(new RectF32(0, 0, RailWidth, 600), items, GuiAppState.TabOrder[0]);

        var nodes = new System.Collections.Generic.List<Layout.ArrangedNode<float>>();
        bar.CollectPaintedNodes(nodes);

        var cells = nodes
            .Select(n => n.Node.Hit)
            .OfType<HitResult.ListItemHit>()
            .Where(h => h.ListId == TabBarRegions.Tabs)
            .Select(h => h.Index)
            .ToArray();

        cells.ShouldBe(Enumerable.Range(0, items.Length).ToArray());
    }

    [Fact]
    public void ALockedCellReportsTheDisabledListAndSoClaimsNoChord()
    {
        var (renderer, bar) = Build();
        using var _ = renderer;

        // Exactly the rail's own rule: everything but Equipment is locked until a profile exists.
        var items = GuiAppState.TabOrder
            .Select(tab => new TabItem<GuiTab>(tab.ToString(), tab)
            {
                Icon = "#",
                IsEnabled = tab == GuiTab.Equipment,
            })
            .ToArray();
        bar.Render(new RectF32(0, 0, RailWidth, 600), items, GuiTab.Equipment);

        var nodes = new System.Collections.Generic.List<Layout.ArrangedNode<float>>();
        bar.CollectPaintedNodes(nodes);

        var enabled = nodes
            .Select(n => n.Node.Hit)
            .OfType<HitResult.ListItemHit>()
            .Where(h => h.ListId == TabBarRegions.Tabs)
            .Select(h => h.Index)
            .ToArray();

        var equipmentIndex = System.Array.IndexOf(GuiAppState.TabOrder.ToArray(), GuiTab.Equipment);
        enabled.ShouldBe([equipmentIndex]);

        nodes.Select(n => n.Node.Hit)
            .OfType<HitResult.ListItemHit>()
            .Count(h => h.ListId == TabBarRegions.DisabledTabs)
            .ShouldBe(items.Length - 1);
    }
}
