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
/// that index into a <see cref="GuiTab"/> for the one thing still done afterwards: re-labelling the hit
/// as <c>Tab:&lt;name&gt;</c> so click-by-label can drive it.
/// </para>
/// <para>
/// <b>The chord and the handler are no longer among them, and that is why this file grew.</b> They used
/// to be re-stated per frame in <c>CollectPaintedNodes</c>, which is Vulkan-only, so these remarks said
/// none of it was testable here and the tests below could pin only the SHAPE a rail cell arrives in.
/// DIR.Lib 9.5 put both on <see cref="TabItem{T}"/>, so the bar under test -- an ordinary
/// <c>TabBar&lt;RgbaImage&gt;</c> -- now carries them itself and they can be read straight off the
/// painted node.
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

    /// <summary>
    /// The item's chord reaches the painted NODE, which is the only place the router looks.
    /// </summary>
    /// <remarks>
    /// A chord that stayed on the item and never reached the tree would be silent in exactly the way a
    /// keyboard binding cannot afford: nothing throws, nothing logs, the key simply does nothing.
    /// </remarks>
    [Fact]
    public void ARailCellCarriesItsOwnChordAndItsOwnHandler()
    {
        var (renderer, bar) = Build();
        using var _ = renderer;

        var chosen = new System.Collections.Generic.List<GuiTab>();
        var items = GuiAppState.TabOrder
            .Select((tab, i) => new TabItem<GuiTab>(tab.ToString(), tab)
            {
                Icon = "#",
                Shortcut = new KeyChord(InputKey.F1 + i, InputModifier.Ctrl),
                OnSelect = chosen.Add,
            })
            .ToArray();
        bar.Render(new RectF32(0, 0, RailWidth, 600), items, GuiAppState.TabOrder[0]);

        var nodes = new System.Collections.Generic.List<Layout.ArrangedNode<float>>();
        bar.CollectPaintedNodes(nodes);

        var cells = nodes
            .Where(n => n.Node.Hit is HitResult.ListItemHit { ListId: TabBarRegions.Tabs })
            .OrderBy(n => ((HitResult.ListItemHit)n.Node.Hit!).Index)
            .ToArray();

        cells.Length.ShouldBe(items.Length);
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i].Node.Shortcut.ShouldBe(new KeyChord(InputKey.F1 + i, InputModifier.Ctrl),
                $"cell {i} carries the chord its item declared");
        }

        // And the handler knows WHICH tab, which is the half an index-keyed callback cannot state.
        cells[2].Node.OnClick!(InputModifier.None);
        chosen.ShouldBe([GuiAppState.TabOrder[2]]);
    }

    /// <summary>
    /// A LOCKED cell carries neither, so its chord is inert by absence rather than by a guard beside the
    /// key -- and the guard is the thing that goes missing when a tab is added.
    /// </summary>
    [Fact]
    public void ALockedCellCarriesNeitherChordNorHandler()
    {
        var (renderer, bar) = Build();
        using var _ = renderer;

        var ran = false;
        var items = GuiAppState.TabOrder
            .Select(tab => new TabItem<GuiTab>(tab.ToString(), tab)
            {
                Icon = "#",
                IsEnabled = tab == GuiTab.Equipment,
                Shortcut = new KeyChord(InputKey.E, InputModifier.Ctrl),
                OnSelect = _ => ran = true,
            })
            .ToArray();
        bar.Render(new RectF32(0, 0, RailWidth, 600), items, GuiTab.Equipment);

        var nodes = new System.Collections.Generic.List<Layout.ArrangedNode<float>>();
        bar.CollectPaintedNodes(nodes);

        var locked = nodes
            .Where(n => n.Node.Hit is HitResult.ListItemHit { ListId: TabBarRegions.DisabledTabs })
            .ToArray();

        locked.Length.ShouldBe(items.Length - 1);
        locked.ShouldAllBe(n => n.Node.Shortcut == null);
        locked.ShouldAllBe(n => n.Node.OnClick == null);
        ran.ShouldBeFalse();
    }
}
