using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

// The workbench screen's frame as docs/designs/workbench/Main.dc.html draws it (#2553 section 3): the toolbar, the project tree, the panel
// slot, the canvas and the status strip. Every mark and every word comes from the actions, which have them from the runner's answer.
public sealed partial class WorkbenchScreen<TSurface>
{
    private const float TreeWidth = 300f;
    private const float ToolbarHeight = 44f;
    private const float StatusHeight = 36f;
    private const float ProgressWidth = 200f;
    private const float PlaySize = 22f;
    private const float PlayNumberWidth = 18f;
    private const float EmptyStateWidth = 420f;

    /// <summary>The hit of the stretch choice's box.</summary>
    internal const string StretchChoiceHit = "Stretch:Choose";

    /// <summary>The hit of the workflow title's box.</summary>
    internal const string WorkflowChoiceHit = "Workflow:Switch";

    /// <summary>The hit of the + Step button.</summary>
    internal const string AddStepHit = "Tree:AddStep";

    /// <summary>The hit of a step row's ▶ is this followed by the image and the step's place in it.</summary>
    internal const string PlayHitPrefix = "Play:";

    private readonly DropdownMenuState<string> _stretchMenu = new();
    private readonly DropdownMenuState<string> _workflowMenu = new();
    private readonly DropdownMenuState<string> _addMenu = new();
    private readonly ListScrollController _treeScroll = new();
    private ViewerState? _viewerState;

    /// <summary>Where the panel slot was placed by the last frame, in device pixels: between the tree and the canvas.</summary>
    internal RectF32 PanelRect
    {
        get
        {
            var width = PanelWidth * DpiScale;
            return new RectF32(CanvasRect.X - width, CanvasRect.Y, width, CanvasRect.Height);
        }
    }

    // The closed box of a menu: its label and the small chevron at the text's size on the control fill, one control tall. A press toggles
    // the menu, as the engine's own box does (whose caret is a filled triangle).
    private static Layout.Node MenuBox(string label, RGBAColor32 ink, DropdownMenuState<string> menu, string hit, string tooltip, string? disabled = null)
    {
        var palette = GuiTheme.Palette;
        var box = Layout.Builder.HStack(
                Layout.Builder.Text(label, BaseFontSize, ink, trim: TextTrim.End).WStar(),
                Layout.Builder.Text("⌄", BaseFontSize, palette.DimText, TextAlign.Center).WAuto())
            .WithGap(4f).CrossCenter().Pad(Gap, 0f).Bg(palette.ControlFill).BgHover(GuiTheme.Hover(palette.ControlFill))
            .Border(palette.Separator, 1f, 4f)
            .WStar().HFixed(GuiTheme.Metrics.ButtonHeight)
            .WithTooltip(tooltip)
            .Clickable(new HitResult.ButtonHit(hit), _ => menu.Popover.Toggle(), CursorKind.Pointer);
        return disabled is null ? box : box.Disabled(disabled);
    }

    // A menu's list over everything the frame painted, hung under the box whose hit this is, where this frame arranged it.
    private void RenderMenu(DropdownMenuState<string> menu, string hit, ImmutableArray<Layout.ArrangedNode<float>> arranged, float width, float height)
    {
        if (!menu.IsOpen)
        {
            return;
        }

        var box = arranged.FirstOrDefault(a => a.Node.Hit is HitResult.ButtonHit hitResult && hitResult.Action == hit);
        if (box.Node is null)
        {
            menu.Close();
            return;
        }

        var anchor = new RectF32(box.Bounds.X, box.Bounds.Y, Scale.ToDesignX(box.Bounds.Width), box.Bounds.Height);
        var below = Scale.ToDesignY(height - (box.Bounds.Y + box.Bounds.Height));
        RenderLayout(Layout.Builder.Dropdown(anchor, menu, GuiTheme.Theme, maxHeight: MathF.Max(BaseFontSize, below)),
            new RectF32(0, 0, width, height));
    }

    // ---- Toolbar ----

    // One accent Run to the selected step, Stop, the picture choice, Fit, 1:1 and the stretch: one height and one middle.
    private Layout.Node Toolbar(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var running = actions.Jobs.IsRunning;
        var offer = actions.RunToHereOffer();
        var title = actions.SelectedStepTitle;
        var runLabel = title is null ? "▶ Run" : $"▶ Run to {ShortTitle(title)}";
        // The accent is the colour of an action that can be taken: a run that cannot go is a plain, dimmed button that says why.
        var canRun = !running && offer.CanRun;
        var run = Layout.Builder.Button(runLabel, canRun ? Layout.ButtonKind.Accent : Layout.ButtonKind.Standard, actions.RunToHere, GuiTheme.Theme,
            tooltip: offer.CanRun ? $"Make {title ?? "the selected step"}, taking each step before it from the store where it is kept" : offer.Label).WAuto();
        var stop = Layout.Builder.Button("■ Stop", Layout.ButtonKind.Standard, actions.CancelRun, GuiTheme.Theme,
            tooltip: "End the run; the steps then show what the runner says of them").WAuto();
        var parts = new List<Layout.Node>
        {
            canRun ? run : run.Disabled(running ? "A run is going" : offer.Label),
            running ? stop : stop.Disabled("No run is going"),
            Layout.Builder.Spacer().WStar(),
            ViewChoice(actions),
            Layout.Builder.Fill().WFixed(1f).HFixed(BaseFontSize * 1.4f).Bg(palette.Separator),
            Layout.Builder.Button("Fit", Layout.ButtonKind.Standard, () => { if (_viewerState is { } v) { ViewerActions.ZoomToFit(v); } }, GuiTheme.Theme,
                tooltip: "Fit the picture to the canvas").WAuto(),
            Layout.Builder.Button("1:1", Layout.ButtonKind.Standard, () => { if (_viewerState is { } v) { ViewerActions.ZoomToActual(v); } }, GuiTheme.Theme,
                tooltip: "Show one picture pixel for each screen pixel").WAuto(),
            StretchChoice(),
        };
        return Layout.Builder.HStack([.. parts]).WithGap(Gap).CrossCenter().Pad(Gap * 1.5f, 0f).Bg(palette.HeaderBg);
    }

    // Output, Input and Before and after as one choice. Output is always there (a step that made none says so on the canvas); the input
    // and the comparison are offered where the step has a file for each.
    private Layout.Node ViewChoice(WorkbenchActions actions)
    {
        var offer = actions.ViewOffer();
        var kinds = new List<Layout.ButtonGroupOption<WorkbenchViewKind>>
        {
            new(WorkbenchViewKind.Result, "Output") { Hit = new HitResult.ButtonHit("View:Result") },
            new(WorkbenchViewKind.Original, "Input") { Hit = new HitResult.ButtonHit("View:Original") },
        };
        if (offer.ComparisonOffered)
        {
            kinds.Add(new(WorkbenchViewKind.Comparison, "Before and after") { Hit = new HitResult.ButtonHit("View:Comparison") });
        }

        var shown = actions.ShowsEmptyState ? WorkbenchViewKind.Result : actions.EffectiveView();
        return Layout.Builder.Segmented<WorkbenchViewKind>([.. kinds], shown, actions.ChooseView, GuiTheme.Theme,
            _viewCursor, "WorkbenchViews", BaseFontSize).WAuto();
    }

    // A step's title can be a sentence: the buttons carry the start of it, the tooltip and the panel the whole.
    private static string ShortTitle(string title) => title.Length > 28 ? title[..27] + "…" : title;

    private static string StretchLabel(StretchMode mode) => mode switch
    {
        StretchMode.Auto => "Auto",
        StretchMode.Linked => "Linked",
        StretchMode.Unlinked => "Unlinked",
        StretchMode.Luma => "Luma",
        StretchMode.Planetary => "Planetary",
        _ => "Linear",
    };

    private Layout.Node StretchChoice()
    {
        var viewerState = _viewerState;
        _stretchMenu.Items = [.. ViewerActions.StretchLinkModes.Select(static mode => DropdownItem.Text(StretchLabel(mode)))];
        _stretchMenu.OnSelect = item =>
        {
            var chosen = ViewerActions.StretchLinkModes.FirstOrDefault(mode => StretchLabel(mode) == item.Value);
            if (viewerState is not null)
            {
                ViewerActions.ChooseStretchLink(viewerState, chosen);
            }
        };
        var current = StretchLabel(viewerState?.StretchModeOn ?? ViewerActions.DefaultStretchMode);
        return MenuBox($"Stretch: {current}", GuiTheme.Palette.BodyText, _stretchMenu, StretchChoiceHit, "How the picture is stretched for the screen").WFixed(150f);
    }

    // ---- Tree ----

    private Layout.Node Tree(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var rows = new List<Layout.Node>();
        var items = actions.Tree();
        var frames = state.Frames;

        // The Frames section: each group selectable, with its count. Reading says so until the capture folder has answered.
        rows.Add(SectionLabel("FRAMES"));
        if (frames is null)
        {
            rows.Add(Words(state.View is null ? "Open a project to see its frames." : actions.HasCaptureFolder ? "Reading the capture folder." : "This project was not made from a capture folder, so it has no frames to list.", palette.DimText, FlowRoom.Tree).Pad(Gap, 0f));
        }
        else if (frames.Groups.IsEmpty)
        {
            rows.Add(Words("The capture folder could not be read.", palette.DimText, FlowRoom.Tree).Pad(Gap, 0f));
        }

        foreach (var group in frames?.Groups ?? [])
        {
            var selected = state.SelectedFrames == group.Name;
            var fill = selected ? palette.Selection : palette.PanelBg;
            var ink = selected ? palette.SelectionText : group.Count > 0 ? palette.BodyText : palette.DimText;
            var name = group.Name;
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(group.Name, BaseFontSize, ink, trim: TextTrim.End).WStar(),
                    Layout.Builder.Text(group.Count.ToString(CultureInfo.InvariantCulture), BaseFontSize * 0.9f, selected ? palette.SelectionText : palette.DimText, TextAlign.Far).WAuto())
                .WithGap(Gap).CrossCenter().Pad(Gap, 0f).Bg(fill).BgHover(GuiTheme.Hover(fill)).Radius(4f)
                .WStar().HFixed(TreeRowHeight)
                .WithTooltip($"{group.Name}: {group.Count} {(group.Count == 1 ? "file" : "files")}")
                .Clickable(new HitResult.ButtonHit($"Frames:{name}"), _ => actions.SelectFrames(name), CursorKind.Pointer));
        }

        rows.Add(SectionLabel("WORKFLOW").Pad(0f, Gap));
        if (items.IsEmpty)
        {
            rows.Add(Words(state.View is { Refusal: { } refusal } ? refusal : state.View is null ? "No project is open." : "This project has no steps yet.",
                palette.DimText, FlowRoom.Tree).Pad(Gap, 0f));
        }

        foreach (var item in items)
        {
            rows.Add(TreeRow(item, state, actions));
        }

        return Layout.Builder.VStack(
                TreeHeader(state, actions).WStar(),
                Layout.Builder.VStack([.. rows]).WithGap(1f).Pad(Gap / 2, Gap / 2).WithScroll(_treeScroll).WStar().HStar(),
                Words("✓ made · ↻ stale · ○ not made · ⚠ refused · ▶ runs every step up to it", palette.DimText, FlowRoom.Tree).Pad(Gap, Gap / 2))
            .Bg(palette.PanelBg);
    }

    private static Layout.Node SectionLabel(string text)
        => Layout.Builder.Text(text, BaseFontSize * 0.8f, GuiTheme.Palette.DimText).WStar().Pad(Gap, 0f);

    // The workflow's name with the dropdown that switches it, then + Step and Remove; the question a switch asks before it replaces the
    // person's changes, and why the last change was refused, when there is one.
    private Layout.Node TreeHeader(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var waits = actions.Jobs.IsRunning ? "A run is going: the steps can be changed when it ends" : null;
        _workflowMenu.Items = [.. BuiltInWorkflows.All.Select(static t => DropdownItem.Text(t.Title))];
        _workflowMenu.OnSelect = item =>
        {
            if (BuiltInWorkflows.All.FirstOrDefault(t => t.Title == item.Value) is { } template)
            {
                actions.SwitchWorkflow(template.Id);
            }
        };
        _addMenu.Items = [.. actions.AddableSteps().Select(static t => DropdownItem.Text(t.Title))];
        _addMenu.OnSelect = item =>
        {
            if (actions.AddableSteps().FirstOrDefault(t => t.Title == item.Value) is { Id: { } id })
            {
                actions.AddStepAfterSelected(id);
            }
        };
        var hasProject = state.View?.Project is not null;
        var canRemove = state.SelectedFrames is null && state.SelectedStep >= 0;
        var add = Layout.Builder.Button("+ Step ›", Layout.ButtonKind.Standard, () => _addMenu.Popover.Toggle(), GuiTheme.Theme,
            tooltip: "Add a step after the selected one, from the operations there are", hit: new HitResult.ButtonHit(AddStepHit)).WAuto();
        var remove = Layout.Builder.Button("Remove", Layout.ButtonKind.Standard, actions.RemoveSelected, GuiTheme.Theme,
            tooltip: "Remove the selected step").WAuto();
        var rows = new List<Layout.Node>
        {
            MenuBox(actions.WorkflowTitle, palette.BodyText, _workflowMenu, WorkflowChoiceHit,
                "The workflow this project's steps came from. Choose another to switch to it", hasProject ? waits : "No project is open").Pad(Gap, 0f),
            Layout.Builder.HStack(
                    waits is not null || !hasProject ? add.Disabled(waits ?? "No project is open") : add,
                    waits is not null || !canRemove ? remove.Disabled(waits ?? "Select a step to remove it") : remove)
                .WithGap(Gap).CrossCenter().Pad(Gap, 0f).WStar(),
        };
        if (state.PendingSwitch is { } pending)
        {
            rows.Add(Layout.Builder.VStack(
                    Words(WorkbenchActions.SwitchWords(pending.Changes), palette.BodyText, FlowRoom.Tree),
                    Layout.Builder.HStack(
                            Layout.Builder.Button("Switch workflow", Layout.ButtonKind.Accent, actions.ConfirmSwitch, GuiTheme.Theme).WAuto(),
                            Layout.Builder.Button("Keep my steps", Layout.ButtonKind.Standard, actions.CancelSwitch, GuiTheme.Theme).WAuto())
                        .WithGap(Gap).WStar())
                .WithGap(Gap / 2).Pad(Gap).Bg(palette.RaisedBg).Radius(6f).WStar());
        }

        if (state.TreeRefusal is { } refusal)
        {
            rows.Add(Words(refusal, palette.Error, FlowRoom.Tree).Pad(Gap, 0f));
        }

        return Layout.Builder.VStack([.. rows]).WithGap(Gap).Pad(0f, Gap);
    }

    // A step: its number, its mark from the runner's answer, its title, and a ▶ that runs every step up to it. The ▶'s room is always
    // there, so nothing moves when the pointer comes. A split and a join say so; a branch is drawn one level in.
    private Layout.Node TreeRow(WorkbenchTreeRow item, WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var row = item.Row;
        var indent = Layout.Builder.Spacer().WFixed(row.Depth * TreeIndent).HStar();
        if (row.EditIndex < 0)
        {
            return Layout.Builder.HStack(indent,
                    Layout.Builder.Text(row.Label, BaseFontSize, palette.DimText, trim: TextTrim.End).WStar())
                .CrossCenter().Pad(Gap, 0f).WStar().HFixed(TreeRowHeight).WithTooltip(row.Label);
        }

        var selected = item.Selected;
        var fill = selected ? palette.Selection : palette.PanelBg;
        var ink = selected ? palette.SelectionText : item.State is StepState.Off ? palette.DimText : palette.BodyText;
        var markInk = selected ? palette.SelectionText : item.State switch
        {
            StepState.Kept or StepState.Ran => palette.Success,
            StepState.Refused or StepState.Blocked => palette.Error,
            StepState.WillRun when item.Mark == "↻" => palette.Warn,
            _ => palette.DimText,
        };
        var note = row.Kind switch
        {
            WorkflowRowKind.Split => "splits",
            WorkflowRowKind.Join => "joins",
            _ => string.Empty,
        };
        var (image, index) = (row.Image, row.EditIndex);
        var current = string.Equals(image, state.View?.ImageName, StringComparison.Ordinal);
        var hit = current ? $"Step:{index}" : $"TreeStep:{image}:{index}";
        var hover = $"{row.Label}: {item.Words}{(note.Length > 0 ? $" ({note})" : string.Empty)}"
            + (row.Detail.Length > 0 ? $". {row.Detail}" : string.Empty);
        // The mark is drawn on the selected row and, by DrawHoveredPlayMarks, on the row under the pointer: every row showing one was noise.
        // The slot itself is always there and always lights, so nothing moves when the pointer comes.
        Layout.Node play = item.Runs
            ? Layout.Builder.Text(selected ? "▶" : string.Empty, BaseFontSize * 0.75f, GuiTheme.GoButtonInk, TextAlign.Center, TextAlign.Center)
                .WFixed(PlaySize).HFixed(PlaySize).Bg(selected ? GuiTheme.GoButtonBg : fill).BgHover(GuiTheme.GoButtonBg).Radius(4f)
                .WithTooltip($"Run every step up to {row.Label}; steps already made are kept")
                .Clickable(new HitResult.ButtonHit(PlayHitPrefix + $"{image}:{index}"), _ => actions.RunToRow(image, index), CursorKind.Pointer)
            : Layout.Builder.Spacer().WFixed(PlaySize).HFixed(PlaySize);
        return Layout.Builder.HStack(
                indent,
                Layout.Builder.Text(item.Number.ToString(CultureInfo.InvariantCulture), BaseFontSize * 0.85f, selected ? palette.SelectionText : palette.DimText, TextAlign.Far)
                    .WFixed(PlayNumberWidth),
                Layout.Builder.Text(item.Mark, BaseFontSize, markInk, TextAlign.Center).WFixed(16f),
                Layout.Builder.Text(row.Label, BaseFontSize, ink, trim: TextTrim.End).WStar(),
                Layout.Builder.Text(note, BaseFontSize * 0.85f, selected ? palette.SelectionText : palette.DimText, TextAlign.Far).WAuto(),
                play)
            .WithGap(4f).CrossCenter().Pad(Gap / 2, 0f).Bg(fill).BgHover(GuiTheme.Hover(fill)).Radius(4f)
            .WStar().HFixed(TreeRowHeight + 2f)
            .WithTooltip(hover)
            .Clickable(new HitResult.ButtonHit(hit), _ => actions.SelectTreeRow(image, index), CursorKind.Pointer);
    }

    // The ▶ of the row under the pointer, drawn over its slot (a layout node has no ink of its own for hover). The row is the slot's height
    // and the tree's width, so the pointer anywhere on the row shows it.
    private void DrawHoveredPlayMarks(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        foreach (var node in arranged)
        {
            if (node.Node.Hit is not HitResult.ButtonHit { Action: { } action } || !action.StartsWith(PlayHitPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var b = node.Bounds;
            if (b.Width <= 0f || !PointerHovers(new RectF32(0f, b.Y - 1f, TreeWidth * DpiScale, b.Height + 2f)))
            {
                continue;
            }

            DrawText("▶", FontPath, b.X, b.Y, b.Width, b.Height, BaseFontSize * 0.75f * DpiScale, GuiTheme.GoButtonInk, TextAlign.Center, TextAlign.Center);
        }
    }

    // ---- Panel slot: a frame group's files (the selected step's panel is Panel) ----

    // The group's files, one row each: its name (a click shows it on the canvas), what it is taken for (the dropdown of the Import page, saved
    // through the same Lib routine) and its grade where the lights were graded. The selected file's row is marked.
    private Layout.Node FramesPanel(WorkbenchFrameGroup group, WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var rows = new List<Layout.Node>
        {
            Words(group.Name, palette.BodyText, FlowRoom.Panel, BaseFontSize * 1.15f),
            Words($"{group.Count} {(group.Count == 1 ? "file" : "files")} in {state.Frames?.Folder}. Press a file to see it; change what a file is taken for in its box.", palette.DimText),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DetectedFile? selectedFile = null;
        foreach (var file in group.Details)
        {
            seen.Add(file.Place);
            var picked = string.Equals(state.PreviewFrame, file.FullPath, StringComparison.OrdinalIgnoreCase);
            var fill = picked ? palette.Selection : palette.PanelBg;
            var ink = picked ? palette.SelectionText : palette.BodyText;
            var full = file.FullPath;
            // The grade is a short word on the file's row, dim when the file was not graded, as the Import table has it.
            var graded = file.Grade is { } g && (g.Causes != GradeCause.None || g.Share > 0f);
            var gradeWord = file.Grade is { } given ? GradeWords(given) : "not graded";
            var gradeInk = picked ? palette.SelectionText : file.Grade is { Causes: GradeCause.None, Share: > 0f } ? palette.Success
                : graded ? palette.Warn : palette.DimText;
            var name = Layout.Builder.Text(file.Place, BaseFontSize, ink, trim: TextTrim.Start).WStar().HStar()
                .WithTooltip(file.FullPath)
                .Clickable(new HitResult.ButtonHit("Frame:" + file.Place), _ => actions.ShowFrameFile(full), CursorKind.Pointer);
            var gradeCell = Layout.Builder.Text(gradeWord, BaseFontSize * 0.85f, gradeInk, TextAlign.Far, trim: TextTrim.End)
                .WFixed(80f).WithTooltip($"Grade: {gradeWord}");
            var line = Layout.Builder.HStack(name, gradeCell, KindCell(file, actions.ReassignFrame).WFixed(110f).HStar())
                .WithGap(Gap / 2).CrossCenter().Pad(Gap / 2, 0f).Bg(fill).BgHover(GuiTheme.Hover(fill)).Radius(4f).WStar().HFixed(TreeRowHeight + 6f);
            rows.Add(line);
            if (picked)
            {
                selectedFile = file;
            }
        }

        // The selected file's details, a few dim lines: what the picture is not told, and why it is taken for what it is.
        if (selectedFile is { } chosen)
        {
            rows.Add(Heading("Selected file"));
            rows.Add(Words(chosen.Place, palette.BodyText));
            var facts = new List<string>();
            if (chosen.ExposureSeconds > 0)
            {
                facts.Add($"{chosen.ExposureSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s");
            }

            if (chosen.Filter.Length > 0)
            {
                facts.Add(chosen.Filter);
            }

            if (float.IsFinite(chosen.SensorTemperatureC))
            {
                facts.Add($"{chosen.SensorTemperatureC.ToString("0.#", CultureInfo.InvariantCulture)} °C");
            }

            if (chosen.Gain > 0)
            {
                facts.Add($"gain {chosen.Gain.ToString(CultureInfo.InvariantCulture)}");
            }

            if (chosen.Camera.Length > 0)
            {
                facts.Add(chosen.Camera);
            }

            if (facts.Count > 0)
            {
                rows.Add(Words(string.Join(", ", facts), palette.DimText));
            }

            if (chosen.Why.Length > 0)
            {
                rows.Add(Words($"Taken for {chosen.Kind}: {chosen.Why}", palette.DimText));
            }

            if (chosen.Note is { Length: > 0 } note)
            {
                rows.Add(Words(note, palette.Warn));
            }
        }

        // Files the detection has not described yet (or a project whose frames were given by place only) are listed by their places.
        if (group.Details.IsEmpty)
        {
            foreach (var file in group.Files)
            {
                rows.Add(Layout.Builder.Text(file, BaseFontSize, palette.BodyText, trim: TextTrim.Start).WStar().WithTooltip(file));
            }
        }

        foreach (var stale in _kindMenus.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _kindMenus.Remove(stale);
        }

        if (group.Count == 0)
        {
            rows.Add(Words("The capture folder holds no file of this kind.", palette.DimText));
        }

        return Column(rows);
    }

    // ---- Canvas ----

    // A step with no output of the kind chosen: says so, what running it will do with the step numbers kept, and offers its ▶.
    private Layout.Node EmptyState(WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var title = actions.SelectedStepTitle ?? "This step";
        var offer = actions.RunToHereOffer();
        var canRun = offer.CanRun && !actions.Jobs.IsRunning;
        // A standard button: the one accent of the view is the toolbar's Run to <step>.
        var run = Layout.Builder.Button($"▶ Run to {ShortTitle(title)}", Layout.ButtonKind.Standard, actions.RunToHere,
            GuiTheme.Theme, tooltip: offer.CanRun ? $"Make {title}, taking each step before it from the store where it is kept" : offer.Label).WAuto();
        var card = Layout.Builder.VStack(
                Layout.Builder.Text($"{ShortTitle(title)} has no output yet", BaseFontSize * 1.15f, palette.BodyText, TextAlign.Center, trim: TextTrim.End)
                    .WStar().WithTooltip($"{title} has no output yet"),
                Words(actions.EmptyStateWords(), palette.DimText, FlowRoom.Preview),
                Layout.Builder.HStack(canRun ? run : run.Disabled(actions.Jobs.IsRunning ? "A run is going" : offer.Label)).WStar())
            .WithGap(Gap * 1.5f);
        // Centred on the canvas, in a column of its own width so the words wrap rather than run across a wide canvas.
        return Layout.Builder.VStack(
                Layout.Builder.Spacer().HStar(),
                Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), card.WFixed(EmptyStateWidth), Layout.Builder.Spacer().WStar()).WStar(),
                Layout.Builder.Spacer().HStar())
            .Bg(palette.ContentBg).WStar().HStar();
    }

    // ---- Status strip ----

    // The last run and its progress, and the log behind a disclosure.
    private Layout.Node StatusStrip(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var (words, fraction, failed) = actions.LastRun();
        var (made, total) = actions.MadeCount();
        var done = total == 0 ? 0f : (float)made / total;
        var shown = fraction is { } f ? (float)Math.Clamp(f, 0d, 1d) : done;
        var bar = Layout.Builder.HStack(
                Layout.Builder.Fill().WStar(Math.Max(shown, 0.001f)).HStar().Bg(failed ? palette.Error : palette.Success),
                Layout.Builder.Fill().WStar(Math.Max(1f - shown, 0.001f)).HStar().Bg(palette.RaisedBg))
            .WFixed(ProgressWidth).HFixed(BaseFontSize * 0.5f).Radius(3f);
        var disclosure = Layout.Builder.Button(state.LogOpen ? "Log ⌃" : "Log ⌄", Layout.ButtonKind.Standard, actions.ToggleLog, GuiTheme.Theme,
            tooltip: state.LogOpen ? "Hide the log" : "Show every line the last run said").WAuto();
        var strip = Layout.Builder.HStack(
                Layout.Builder.Text("Last run:", BaseFontSize * 0.9f, palette.DimText).WAuto(),
                Layout.Builder.Text(words, BaseFontSize * 0.9f, failed ? palette.Error : palette.BodyText, trim: TextTrim.End).WStar().WithTooltip(words),
                bar,
                Layout.Builder.Text(total == 0 ? string.Empty : $"{made} of {total} made", BaseFontSize * 0.9f, palette.DimText, TextAlign.Far).WAuto(),
                disclosure)
            .WithGap(Gap).CrossCenter().Pad(Gap * 1.5f, 0f).Bg(palette.HeaderBg).WStar().HFixed(StatusHeight);
        if (!state.LogOpen)
        {
            return strip;
        }

        return Layout.Builder.VStack(LogLines(actions).WStar().HFixed(JobLinesHeight), strip).WStar();
    }

    // Every line the run said, newest last, in a scroll of its own.
    private Layout.Node LogLines(WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var lines = actions.Jobs.State.Lines;
        if (lines.IsEmpty)
        {
            return Words("The run has said nothing yet.", palette.DimText, FlowRoom.JobLines).Pad(Gap).Bg(palette.ContentBg);
        }

        var nodes = new Layout.Node[lines.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = Words(lines[i], palette.DimText, FlowRoom.JobLines);
        }

        if (lines.Length != _jobLinesDrawn)
        {
            // A new line scrolls the list to its end, so the newest is the one in view.
            _jobLinesDrawn = lines.Length;
            _jobScroll.ScrollToEnd();
        }

        return Layout.Builder.VStack(nodes).WithGap(Gap / 2).Pad(Gap).Bg(palette.ContentBg).WithScroll(_jobScroll);
    }
}
