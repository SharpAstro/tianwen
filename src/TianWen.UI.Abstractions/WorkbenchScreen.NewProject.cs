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

// The two pages of New project as the owner's mockups draw them (docs/designs/workbench/Import.dc.html and Workflow.dc.html):
// what is in the capture folder, then which workflow to start from. Each is one layout tree built from what WorkbenchImport says,
// which the actions set from the Lib routines' answers; nothing here asks the disk or a routine.
public sealed partial class WorkbenchScreen<TSurface>
{
    private const float StripHeight = 56f;
    private const float FooterHeight = 64f;
    private const float GroupsWidth = 300f;
    private const float PreviewWidth = 460f;
    private const float RefusalHeight = 28f;
    private const float TreeRowHeight = 26f;
    private const float TreeIndent = 18f;

    /// <summary>The hit of a file's kind dropdown is this followed by the file's place.</summary>
    internal const string KindHitPrefix = "Kind:";

    private readonly ListScrollController _groupScroll = new();
    private readonly ListScrollController _fileScroll = new();
    private readonly ListScrollController _workflowScroll = new();
    private readonly ListScrollController _previewScroll = new();
    private readonly Dictionary<string, DropdownMenuState<string>> _kindMenus = new(StringComparer.Ordinal);
    private string? _openKindMenu;

    // The file table's columns in the order they are given up when the window is narrow: the sensor temperature first, the file's name and
    // its kind never. Each is (key, column, the narrowest it is worth showing at); a star column's width is its minimum.
    private static readonly (string Key, Layout.TableColumn Column, float Min)[] FileColumnSet =
    [
        ("file", new("File", Layout.Sizing.Star(2f), TextAlign.Near), 190f),
        ("kind", new("Is", Layout.Sizing.Fixed(124f), TextAlign.Near), 124f),
        ("why", new("Because", Layout.Sizing.Star(3f), TextAlign.Near), 170f),
        ("grade", new("Grade", Layout.Sizing.Fixed(110f), TextAlign.Near), 110f),
        ("exposure", new("Exposure", Layout.Sizing.Fixed(104f), TextAlign.Far), 104f),
        ("filter", new("Filter", Layout.Sizing.Fixed(84f), TextAlign.Near), 84f),
        ("sensor", new("Sensor", Layout.Sizing.Fixed(88f), TextAlign.Far), 88f),
    ];

    // The width of the window in design units, from the last Render: the columns that fit are shown, the rest are left to the hover of
    // the row's Because column, so a narrow window never cuts a title to an ellipsis.
    private float _designWidth = float.PositiveInfinity;

    private ImmutableArray<(string Key, Layout.TableColumn Column)> FileColumnsFor()
    {
        // The files' own area: the window less the groups column, the page's padding and the scroll bar.
        var room = _designWidth - GroupsWidth - (Gap * 5) - 16f;
        var keep = FileColumnSet.ToList();
        while (keep.Count > 3 && keep.Sum(static c => c.Min) + ((keep.Count - 1) * Gap) > room)
        {
            keep.RemoveAt(keep.Count - 1);
        }

        return [.. keep.Select(static c => (c.Key, c.Column))];
    }

    // The pages of New project: a title bar, the three-step strip, one reserved line for a refusal, the page and its footer.
    private Layout.Node BuildNewProject(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var import = state.Import;
        var onWorkflow = state.Stage == WorkbenchStage.Workflow;
        var refusal = import?.Refusal;
        var refusalText = Layout.Builder.Text(refusal ?? string.Empty, BaseFontSize, palette.Error, trim: TextTrim.End).WStar().HStar();
        return Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text("TianWen Edit", BaseFontSize, palette.BodyText).WAuto(),
                        Layout.Builder.Text("New project", BaseFontSize * 0.9f, palette.DimText).WAuto())
                    .WithGap(Gap * 1.5f).CrossCenter().Pad(Gap * 1.5f, 0f).Bg(palette.HeaderBg).WStar().HFixed(TopBarHeight),
                Layout.Builder.VStack(
                        Strip(import, onWorkflow).WStar().HFixed(StripHeight),
                        Layout.Builder.HStack(refusal is not null ? refusalText.WithTooltip(refusal) : refusalText)
                            .CrossCenter().Pad(Gap * 3, 0f).WStar().HFixed(RefusalHeight))
                    .Bg(palette.PanelBg).WStar().HFixed(StripHeight + RefusalHeight),
                (onWorkflow ? WorkflowPage(import, actions) : ImportPage(import, actions)).WStar().HStar(),
                (onWorkflow ? WorkflowFooter(import, actions) : ImportFooter(import, actions)).WStar().HFixed(FooterHeight))
            .Stretch().Bg(palette.ContentBg);
    }

    // 1 What is in the folder > 2 Choose a workflow > 3 Workbench, with a tick for a step that is done and the folder at the right.
    private Layout.Node Strip(WorkbenchImport? import, bool onWorkflow)
    {
        var palette = GuiTheme.Palette;
        Layout.Node Item(int number, string title, int at)
        {
            var current = number == at;
            var done = number < at;
            Layout.Node mark = done
                ? Layout.Builder.Text("✓", BaseFontSize, palette.Success, TextAlign.Center, TextAlign.Center).WFixed(22f).HFixed(22f)
                : Layout.Builder.Text(number.ToString(CultureInfo.InvariantCulture), BaseFontSize * 0.9f,
                        current ? GuiTheme.GoButtonInk : palette.DimText, TextAlign.Center, TextAlign.Center)
                    .WFixed(22f).HFixed(22f).Bg(current ? GuiTheme.GoButtonBg : palette.ControlFill).Radius(11f);
            return Layout.Builder.HStack(mark, Layout.Builder.Text(title, BaseFontSize, current || done ? palette.BodyText : palette.DimText).WAuto())
                .WithGap(Gap).CrossCenter().WAuto();
        }

        var at = onWorkflow ? 2 : 1;
        var folder = import?.Folder ?? string.Empty;
        return Layout.Builder.HStack(
                Item(1, "What is in the folder", at),
                Layout.Builder.Text("›", BaseFontSize, palette.DimText).WAuto(),
                Item(2, "Choose a workflow", at),
                Layout.Builder.Text("›", BaseFontSize, palette.DimText).WAuto(),
                Item(3, "Workbench", at),
                Layout.Builder.Text(folder, BaseFontSize * 0.9f, palette.DimText, TextAlign.Far, trim: TextTrim.Start).WStar().HStar().WithTooltip(folder))
            .WithGap(Gap * 3).CrossCenter().Pad(Gap * 3, 0f).Bg(palette.PanelBg);
    }

    // ---- Import ----

    private Layout.Node ImportPage(WorkbenchImport? import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        if (import is null || import.Capture is null)
        {
            _kindMenus.Clear();
            var words = import is { Refusal: not null } ? string.Empty : "Reading the folder: the kinds of its frames come from their FITS cards first, then the folder names, then the pixels.";
            return Layout.Builder.Text(words, BaseFontSize, palette.DimText, TextAlign.Center, TextAlign.Center).WStar().HStar().Pad(Gap * 2);
        }

        return Layout.Builder.HStack(Groups(import, actions).WFixed(GroupsWidth).HStar(), Files(import, actions).WStar().HStar())
            .WStar().HStar();
    }

    private static string MarkOf(DetectedGroupKind kind) => kind switch
    {
        DetectedGroupKind.NeedsALook => "⚠",
        DetectedGroupKind.CometTrack => "◆",
        DetectedGroupKind.NotStacked => "–",
        _ => "✓",
    };

    private static RGBAColor32 MarkInk(DetectedGroupKind kind, bool selected)
    {
        var palette = GuiTheme.Palette;
        if (selected)
        {
            return palette.SelectionText;
        }

        return kind switch
        {
            DetectedGroupKind.NeedsALook => palette.Warn,
            DetectedGroupKind.CometTrack => palette.Info,
            DetectedGroupKind.NotStacked => palette.DimText,
            _ => palette.Success,
        };
    }

    // A group the Lib gave no detail line is still given one, so every row is as tall as the others.
    private static string DetailOf(DetectedGroup group) => group.Kind switch
    {
        DetectedGroupKind.CometTrack => group.Files.Length > 0 ? System.IO.Path.GetFileName(group.Files[0].FullPath) : "the comet's track",
        DetectedGroupKind.NeedsALook => "frames to check before stacking",
        DetectedGroupKind.NotStacked => "left out of the stack",
        _ => string.Empty,
    };

    // The groups found, one row each (mark, name, detail, count), and what needs a look said once under them.
    private Layout.Node Groups(WorkbenchImport import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var capture = import.Capture!;
        var rows = new List<Layout.Node>
        {
            Layout.Builder.Text("FOUND", BaseFontSize * 0.8f, palette.DimText).WStar().Pad(Gap, 0f),
        };
        for (var i = 0; i < capture.Groups.Length; i++)
        {
            var group = capture.Groups[i];
            var selected = i == import.SelectedGroup;
            var ink = selected ? palette.SelectionText : palette.BodyText;
            var dim = selected ? palette.SelectionText : palette.DimText;
            var fill = selected ? palette.Selection : palette.PanelBg;
            var at = i;
            // Mark, name and count share one row (one middle); the detail is a line of its own under the name, always there.
            var detail = group.Detail.Length > 0 ? group.Detail : DetailOf(group);
            rows.Add(Layout.Builder.VStack(
                    Layout.Builder.HStack(
                            Layout.Builder.Text(MarkOf(group.Kind), BaseFontSize, MarkInk(group.Kind, selected), TextAlign.Center).WFixed(18f),
                            Layout.Builder.Text(group.Title, BaseFontSize, ink, trim: TextTrim.End).WStar(),
                            Layout.Builder.Text(group.Files.Length.ToString(CultureInfo.InvariantCulture), BaseFontSize, dim, TextAlign.Far).WAuto())
                        .WithGap(Gap).CrossCenter().WStar(),
                    Layout.Builder.HStack(
                            Layout.Builder.Spacer().WFixed(18f),
                            Layout.Builder.Text(detail, BaseFontSize * 0.85f, dim, trim: TextTrim.End).WStar().WithTooltip(detail))
                        .WithGap(Gap).WStar())
                .Pad(Gap, Gap / 2).Bg(fill).BgHover(GuiTheme.Hover(fill)).Radius(4f)
                .WStar()
                .WithTooltip(group.Heading)
                .Clickable(new HitResult.ButtonHit($"Group:{i}"), _ => actions.SelectGroup(at), CursorKind.Pointer));
        }

        if (capture.Notes.Length > 0 || import.NeedsALook > 0)
        {
            rows.Add(Layout.Builder.Spacer().HFixed(Gap));
        }

        if (import.NeedsALook > 0)
        {
            var files = capture.Groups.First(static g => g.Kind == DetectedGroupKind.NeedsALook).Files;
            var guessed = files.Count(static f => f.Unsure);
            var noDark = files.Count(static f => f.Note is not null);
            var parts = new List<string>();
            if (guessed > 0)
            {
                parts.Add($"{guessed} {(guessed == 1 ? "has a kind" : "have a kind")} guessed from the pixels (marked ?).");
            }

            if (noDark > 0)
            {
                parts.Add($"{noDark} {(noDark == 1 ? "light matches" : "lights match")} no dark.");
            }

            rows.Add(Layout.Builder.VStack(
                    Layout.Builder.Text($"⚠ {import.NeedsALook} {(import.NeedsALook == 1 ? "file needs" : "files need")} a look", BaseFontSize, palette.Warn).WStar(),
                    Words(string.Join(' ', parts), palette.DimText, FlowRoom.Groups))
                .WithGap(4f).Pad(Gap).Bg(palette.RaisedBg).Radius(6f).WStar());
        }

        foreach (var note in capture.Notes)
        {
            rows.Add(Words(note, palette.DimText, FlowRoom.Groups).Pad(Gap, 0f));
        }

        return Layout.Builder.VStack([.. rows]).WithGap(4f).Pad(Gap * 1.5f, Gap * 2).Bg(palette.PanelBg).WithScroll(_groupScroll);
    }

    // The chosen group's files as a table: the engine lays out the header and the rows from the same columns, so a title stays over its values.
    private Layout.Node Files(WorkbenchImport import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var group = import.Group;
        if (group is null)
        {
            return Layout.Builder.Text("This folder holds no frames.", BaseFontSize, palette.DimText, TextAlign.Center, TextAlign.Center).WStar().HStar();
        }

        var heading = Layout.Builder.HStack(
                Layout.Builder.Text(group.Title, BaseFontSize * 1.15f, palette.BodyText).WAuto(),
                Layout.Builder.Text(group.Detail, BaseFontSize, palette.DimText, trim: TextTrim.End).WStar().WithTooltip(group.Detail))
            .WithGap(Gap * 1.5f).CrossCenter().Pad(Gap * 2.5f, 0f).WStar().HFixed(TopBarHeight);
        const string hint = "Detected from the FITS cards first, then the folder names, then the pixels";
        var hintRow = Layout.Builder.HStack(Layout.Builder.Text(hint, BaseFontSize * 0.85f, palette.DimText, trim: TextTrim.End).WStar().HStar().WithTooltip(hint))
            .Pad(Gap * 2.5f, 0f).WStar().HFixed(BaseFontSize * 1.6f);

        var style = new Layout.TableStyle(palette.PanelBg, palette.DimText, GuiTheme.Hover(palette.PanelBg), palette.Accent) { Gap = Gap };
        var rowHeight = GuiTheme.Metrics.ButtonHeight + Gap;
        var columns = FileColumnsFor();
        var span = columns.Select(static c => c.Column).ToArray();
        var rows = new List<Layout.Node>
        {
            Layout.Builder.TableHeader(span, null, null, in style, BaseFontSize * 0.85f, "Files:").WStar().HFixed(rowHeight),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in group.Files)
        {
            seen.Add(file.Place);
            var cells = FileCells(file, actions);
            rows.Add(Layout.Builder.TableRow(span, [.. columns.Select(c => cells[c.Key])], in style)
                .WStar().HFixed(rowHeight).Bg(file.NeedsALook ? GuiTheme.Mix(palette.ContentBg, palette.Warn, 0.08f) : palette.ContentBg));
        }

        // A menu of a file this page no longer shows is forgotten, so the dictionary is as long as the table, not as the session.
        foreach (var stale in _kindMenus.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _kindMenus.Remove(stale);
        }

        return Layout.Builder.VStack(
                heading,
                hintRow,
                Layout.Builder.VStack([.. rows]).WithGap(1f).WStar().WithScroll(_fileScroll).HStar())
            .WStar().HStar().Bg(palette.ContentBg);
    }

    private static string Seconds(double seconds)
        => seconds > 0 ? $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)} s" : "—";

    // The grade is the Lib's verdict in a word, never a share: good, or the cause that holds it down (LightGrading.Verdict). A frame
    // the Lib could not grade is said not to be graded.
    private static string GradeWords(DetectedGrade grade)
        => grade.Causes != GradeCause.None
            ? string.Join(", ", new[]
            {
                (grade.Causes & GradeCause.Cloud) != 0 ? "maybe cloud" : null,
                (grade.Causes & GradeCause.Sunrise) != 0 ? "sunrise" : null,
                (grade.Causes & GradeCause.Sunset) != 0 ? "sunset" : null,
            }.OfType<string>())
            : grade.Share > 0f ? "good" : "not graded";

    private Dictionary<string, Layout.Node> FileCells(DetectedFile file, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        Layout.Node Cell(string text, RGBAColor32 ink, TextAlign align = TextAlign.Near, string? tooltip = null)
            => Layout.Builder.Text(text, BaseFontSize, ink, align, trim: align == TextAlign.Near ? TextTrim.End : TextTrim.Start).WithTooltip(tooltip ?? text);

        var why = file.Note is { } note ? $"{file.Why} · {note}" : file.Why;
        var grade = file.Grade is { } g ? GradeWords(g) : "—";
        var gradeInk = file.Grade is { Causes: GradeCause.None, Share: > 0f } ? palette.Success : file.Grade is { Causes: not GradeCause.None } ? palette.Warn : palette.DimText;
        return new Dictionary<string, Layout.Node>
        {
            ["file"] = Layout.Builder.Text(file.Place, BaseFontSize, palette.BodyText, trim: TextTrim.Start).WithTooltip(file.FullPath),
            ["kind"] = KindCell(file, actions),
            ["why"] = Cell(why, palette.DimText),
            ["exposure"] = Cell(Seconds(file.ExposureSeconds), palette.BodyText, TextAlign.Far),
            ["filter"] = Cell(file.Filter.Length > 0 ? file.Filter : "—", palette.BodyText),
            ["sensor"] = Cell(float.IsFinite(file.SensorTemperatureC) ? $"{file.SensorTemperatureC.ToString("0.#", CultureInfo.InvariantCulture)} °C" : "—", palette.BodyText, TextAlign.Far),
            ["grade"] = Cell(grade, gradeInk),
        };
    }

    // What the file is taken for, as a dropdown of the kinds a stack takes. The label is the Lib's answer (the kind the detection
    // now gives it, after any reassignment), and a guess carries a question mark, so colour is not the only mark of an unsure kind.
    private Layout.Node KindCell(DetectedFile file, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        if (file.IsCometTrack)
        {
            return Layout.Builder.Text("Comet track", BaseFontSize, palette.Info).WithTooltip(file.Why);
        }

        if (!_kindMenus.TryGetValue(file.Place, out var menu))
        {
            menu = new DropdownMenuState<string>();
            _kindMenus[file.Place] = menu;
        }

        var place = file.Place;
        menu.Items = [.. WorkbenchActions.KindChoices.Select(static choice => DropdownItem.Text(choice.Label))];
        menu.OnSelect = item =>
        {
            var chosen = WorkbenchActions.KindChoices.FirstOrDefault(choice => choice.Label == item.Value);
            actions.Reassign(place, chosen.Kind);
        };
        var label = WorkbenchActions.KindLabel(file.Kind) + (file.Unsure ? "?" : string.Empty) + (file.Reassigned ? " ✎" : string.Empty);
        var tooltip = file.Reassigned ? $"{file.Why}. Press to change it, or to take your answer back." : $"{file.Why}. Press to change it.";
        // The box is as tall as a control with the row's breathing room above and below, and ends in the small chevron at the text's
        // size (the engine's own box ends in a filled caret); a press toggles the menu as the engine's box does.
        var box = MenuBox(label, file.NeedsALook ? palette.Warn : palette.BodyText, menu, KindHitPrefix + place, tooltip);
        return Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), box, Layout.Builder.Spacer().HStar()).WStar().HStar();
    }

    // The menu of the file whose kind box is open, over everything, hung under its box where this frame arranged it. One is open at a
    // time: pressing another box closes the one before it.
    private void RenderKindMenu(ImmutableArray<Layout.ArrangedNode<float>> arranged, float width, float height)
    {
        var open = _kindMenus.Where(static pair => pair.Value.IsOpen).Select(static pair => pair.Key).ToList();
        if (_openKindMenu is { } before && open.Count > 1 && open.Contains(before))
        {
            _kindMenus[before].Close();
            open.Remove(before);
        }

        _openKindMenu = open.FirstOrDefault();
        if (_openKindMenu is not { } place)
        {
            return;
        }

        var menu = _kindMenus[place];
        var box = arranged.FirstOrDefault(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == KindHitPrefix + place);
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

    private Layout.Node ImportFooter(WorkbenchImport? import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var folder = import is null ? string.Empty : TianWen.Lib.Imaging.Workbench.WorkbenchProjects.WorkdirOf(import.Folder);
        var words = $"The project and every result are kept in {folder}; your frames are read, never written.";
        var waits = import switch
        {
            null => "There is no folder on show",
            { Detecting: true } => "The folder is being read",
            { Reassigning: true } => "Your answer is being saved",
            { Workflows.IsDefaultOrEmpty: true } => "The folder has not been read yet",
            _ => null,
        };
        var next = Layout.Builder.Button("Choose a workflow ›", Layout.ButtonKind.Accent, actions.ChooseAWorkflow, GuiTheme.Theme,
            tooltip: waits ?? "Go on to the workflows that fit these frames").WAuto().WithShortcut(InputKey.Enter);
        var cancel = Layout.Builder.Button("Cancel", Layout.ButtonKind.Standard, actions.CancelImport, GuiTheme.Theme,
            tooltip: "Leave New project; the project this made is taken back").WAuto();
        return Layout.Builder.HStack(
                Layout.Builder.Text(words, BaseFontSize * 0.9f, palette.DimText, trim: TextTrim.Middle).WStar().HStar().WithTooltip(words),
                import is { Applying: true } ? cancel.Disabled("The workflow is being written") : cancel,
                waits is not null ? next.Disabled(waits) : next)
            .WithGap(Gap * 1.5f).CrossCenter().Pad(Gap * 3, 0f).Bg(palette.PanelBg);
    }

    // ---- Workflow ----

    private static string StepsWords(WorkflowChoice choice)
        => choice.StepCount == 1 ? "1 step" : $"{choice.StepCount} steps";

    private Layout.Node WorkflowPage(WorkbenchImport? import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        if (import is null)
        {
            return Layout.Builder.Text(string.Empty, BaseFontSize, palette.DimText).WStar().HStar();
        }

        var cards = new List<Layout.Node> { Layout.Builder.Text("FITS THESE FRAMES", BaseFontSize * 0.8f, palette.DimText).WStar() };
        var fitting = import.Workflows.Where(static w => w.Answer.Fit.Fits && w.Id != BuiltInWorkflows.Empty).ToList();
        if (fitting.Count == 0)
        {
            cards.Add(Words("None of the workflows with steps fits these frames; the reasons are below. Start from an empty one and add the steps you want.", palette.DimText, FlowRoom.Cards));
        }

        foreach (var choice in fitting)
        {
            cards.Add(Card(choice, import.ChosenWorkflow == choice.Id, actions));
        }

        var rest = import.Workflows.Where(static w => !w.Answer.Fit.Fits).ToList();
        if (rest.Count > 0)
        {
            cards.Add(Layout.Builder.Text("DOES NOT FIT", BaseFontSize * 0.8f, palette.DimText).WStar().Pad(0f, Gap));
            foreach (var choice in rest)
            {
                cards.Add(Layout.Builder.VStack(
                        Layout.Builder.Text(choice.Answer.Template.Title, BaseFontSize, palette.BodyText, trim: TextTrim.End).WStar()
                            .WithTooltip(choice.Answer.Template.Title),
                        Words(choice.Answer.Fit.Why, palette.DimText, FlowRoom.Cards))
                    .WithGap(4f).Pad(Gap * 1.5f).Bg(palette.PanelBg).Radius(6f).WStar());
            }
        }

        return Layout.Builder.HStack(
                Layout.Builder.VStack([.. cards]).WithGap(Gap * 1.25f).Pad(Gap * 3, Gap * 2.5f).WithScroll(_workflowScroll).WStar().HStar(),
                Preview(import).WFixed(PreviewWidth).HStar())
            .WStar().HStar();
    }

    // A workflow that fits: its name, how many steps, and why in one line. A press selects it, a double-click uses it.
    private Layout.Node Card(WorkflowChoice choice, bool selected, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var fill = selected ? palette.Selection : palette.PanelBg;
        var ink = selected ? palette.SelectionText : palette.BodyText;
        var dim = selected ? palette.SelectionText : palette.DimText;
        var id = choice.Id;
        var title = choice.Answer.Template.Title;
        return Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text(title, BaseFontSize * 1.1f, ink, trim: TextTrim.End).WStar().WithTooltip(title),
                        Layout.Builder.Text(StepsWords(choice), BaseFontSize * 0.9f, dim).WAuto())
                    .WithGap(Gap).CrossCenter().WStar(),
                Words(choice.Answer.Fit.Why, dim, FlowRoom.Cards))
            .WithGap(4f).Pad(Gap * 1.5f).Bg(fill).BgHover(GuiTheme.Hover(fill)).Radius(6f).WStar()
            .WithTooltip($"{title}: {choice.Answer.Template.Summary}")
            .Border(selected ? palette.Accent : fill, 2f, 6f)
            .Clickable(new HitResult.ButtonHit($"Workflow:{id}"), _ => actions.ChooseWorkflow(id), CursorKind.Pointer)
            .DoubleClickable(_ => actions.UseWorkflow(id));
    }

    // The chosen workflow as the tree it becomes. A split's branches are indented one level, a join comes back out.
    private Layout.Node Preview(WorkbenchImport import)
    {
        var palette = GuiTheme.Palette;
        var chosen = import.Workflows.FirstOrDefault(w => w.Id == import.ChosenWorkflow);
        var rows = new List<Layout.Node> { Layout.Builder.Text("WHAT IT WILL DO", BaseFontSize * 0.8f, palette.DimText).WStar() };
        if (chosen is null)
        {
            rows.Add(Words("Choose a workflow to see its steps.", palette.DimText, FlowRoom.Preview));
        }
        else
        {
            var title = chosen.Answer.Template.Title;
            rows.Add(Layout.Builder.Text(title, BaseFontSize * 1.15f, palette.BodyText, trim: TextTrim.End).WStar().WithTooltip(title));
            rows.Add(Words(chosen.Answer.Template.Summary, palette.DimText, FlowRoom.Preview));
            var steps = chosen.Tree.Where(static row => row.Kind is not (WorkflowRowKind.Source or WorkflowRowKind.Image)).ToList();
            if (steps.Count == 0)
            {
                rows.Add(Words("No steps: add them in the workbench.", palette.DimText, FlowRoom.Preview));
            }

            string? previousImage = null;
            foreach (var row in steps)
            {
                // A branch of a split starts under a caption saying which it is ("on stars"), so two branches that both run a Gradient are told
                // apart. The caption is drawn here from the row's image; the Lib's rows are the steps alone.
                if (row.Depth > 0 && row.EditIndex == 0 && row.Image != previousImage)
                {
                    var branch = "on " + row.Image[(row.Image.LastIndexOf('-') + 1)..];
                    rows.Add(Layout.Builder.HStack(
                            Layout.Builder.Spacer().WFixed(row.Depth * TreeIndent).HStar(),
                            Layout.Builder.Text(branch, BaseFontSize * 0.85f, palette.DimText, trim: TextTrim.End).WStar())
                        .WithGap(Gap).CrossCenter().WStar().HFixed(TreeRowHeight).WithTooltip(row.Image));
                }

                previousImage = row.Image;
                var note = row.Kind switch
                {
                    WorkflowRowKind.Split => row.Detail.Length > 0 ? row.Detail + " · splits" : "splits",
                    WorkflowRowKind.Join => row.Detail.Length > 0 ? row.Detail + " · joins" : "joins",
                    _ => row.Detail,
                };
                var full = note.Length > 0 ? $"{row.Label}: {note}" : row.Label;
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(row.Depth * TreeIndent).HStar(),
                        Layout.Builder.Text(row.Kind == WorkflowRowKind.Split ? "◆" : row.Kind == WorkflowRowKind.Join ? "◇" : "•", BaseFontSize, palette.DimText).WFixed(14f),
                        Layout.Builder.Text(row.Label, BaseFontSize, palette.BodyText, trim: TextTrim.End).WStar(2f),
                        Layout.Builder.Text(note, BaseFontSize * 0.85f, palette.DimText, TextAlign.Far, trim: TextTrim.End).WStar())
                    .WithGap(Gap).CrossCenter().WStar().HFixed(TreeRowHeight).WithTooltip(full));
            }

            rows.Add(Words("Every step can be changed, switched off or removed in the workbench, and steps added anywhere.", palette.DimText, FlowRoom.Preview));
        }

        return Layout.Builder.VStack([.. rows]).WithGap(Gap).Pad(Gap * 2.5f).Bg(palette.PanelBg).WithScroll(_previewScroll);
    }

    private Layout.Node WorkflowFooter(WorkbenchImport? import, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var applying = import is { Applying: true };
        var waits = applying ? "The workflow is being written"
            : import?.ChosenWorkflow is null ? "No workflow fits: start from an empty one"
            : null;
        var back = Layout.Builder.Button("‹ Back", Layout.ButtonKind.Standard, actions.BackToImport, GuiTheme.Theme,
            tooltip: "Back to what is in the folder").WAuto();
        var use = Layout.Builder.Button("Use selected workflow ›", Layout.ButtonKind.Accent, () => actions.UseWorkflow(), GuiTheme.Theme,
            tooltip: waits ?? "Make the project with this workflow and open the workbench").WAuto().WithShortcut(InputKey.Enter);
        const string hint = "Or double-click a workflow, or press Enter";
        var empty = Layout.Builder.Button("Start from an empty workflow", Layout.ButtonKind.Standard, () => actions.UseWorkflow(BuiltInWorkflows.Empty),
            GuiTheme.Theme, tooltip: "Make the project with no steps; add them in the workbench").WAuto();
        return Layout.Builder.HStack(
                applying ? back.Disabled(waits!) : back,
                Layout.Builder.Text(hint, BaseFontSize * 0.9f, palette.DimText, TextAlign.Far, trim: TextTrim.End).WStar().HStar().WithTooltip(hint),
                applying ? empty.Disabled("The workflow is being written") : empty,
                waits is not null ? use.Disabled(waits) : use)
            .WithGap(Gap * 1.5f).CrossCenter().Pad(Gap * 3, 0f).Bg(palette.PanelBg);
    }
}
