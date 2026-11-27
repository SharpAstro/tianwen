using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The processing app's guided screen as ONE layout tree: a top bar (the project and the image), the step bar left to right,
/// the selected step's panel at the left and the canvas. The canvas is a keyed fill the engine places; the viewer's own
/// renderer is embedded in it (<c>SetContentRegion</c>), so a master looks the same here as in the viewer.
/// </summary>
/// <remarks>
/// Nothing here is positioned by a sum of constants and nothing is measured by hand: the engine is told what each part is.
/// Colours are the theme's alone, so Night is judged by the palette. There is nothing in the top bar for a screen that does
/// not exist yet (Stack, Process, Workflow).
/// </remarks>
public sealed partial class WorkbenchScreen<TSurface> : CompositeWidget<TSurface>
{
    /// <summary>The key of the fill the viewer is embedded in.</summary>
    internal const string CanvasKey = "WorkbenchCanvas";

    private static float BaseFontSize => GuiTheme.Metrics.BaseFontSize;
    internal const float PanelWidth = 340f;
    private const float Gap = 8f;
    private const float TopBarHeight = 44f;
    private const float JobLinesHeight = 150f;

    private readonly ImageRendererBase<TSurface> _viewer;
    private readonly IReadOnlyList<PixelWidgetBase<TSurface>> _children;
    private readonly ListScrollController _panelScroll = new();
    private readonly ListCursor _viewCursor = new();
    private readonly ListScrollController _jobScroll = new();
    private readonly DropdownMenuState<string> _imageMenu = new();

    /// <summary>The hit of the image choice's box; its menu hangs under where the frame arranged it.</summary>
    internal const string ImageChoiceHit = "Image:Choose";
    private int _jobLinesDrawn;

    /// <param name="renderer">The surface the screen and the viewer draw on.</param>
    /// <param name="viewer">The viewer embedded in the canvas; it takes this widget's window context.</param>
    public WorkbenchScreen(Renderer<TSurface> renderer, ImageRendererBase<TSurface> viewer) : base(renderer)
    {
        _viewer = viewer;
        // The viewer was configured on its own (its face, its scale) before it was embedded, and embedding replaces its
        // context with this widget's: keep what it resolved.
        FontPath = viewer.FontPath;
        EmojiFontPath = viewer.EmojiFontPath;
        DpiScale = viewer.DpiScale;
        _children = [viewer];
        ShareUiContext(viewer);
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<PixelWidgetBase<TSurface>> Children => _onPages ? [] : _children;

    // The pages of New project hold no viewer, so what it painted last is neither drawn nor hit nor listed while they are on show.
    private bool _onPages;

    /// <summary>The viewer is painted over the canvas this widget lays out, after this widget's own tree.</summary>
    protected override int ChildrenUnderOwn => 0;

    /// <summary>Where the canvas was placed by the last frame, in device pixels.</summary>
    public RectF32 CanvasRect { get; private set; }

    /// <summary>
    /// Every text the last frame drew shorter than it is: the whole text, what the painter's own fit shows of it in its arranged
    /// width, and where it was arranged. The capture tests ask that each of these carries its whole text on hover.
    /// </summary>
    internal ImmutableArray<(string Full, string Shown, RectF32 Bounds)> CutTexts()
    {
        var nodes = new List<Layout.ArrangedNode<float>>();
        CollectPaintedNodes(nodes);
        var ctx = MeasureContext();
        var cut = ImmutableArray.CreateBuilder<(string, string, RectF32)>();
        foreach (var node in nodes)
        {
            if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value.Length > 0)
            {
                var shown = TextFit.ForWidth(Renderer, text.Value, ctx.FontPath, ctx.Fallback, text.FontSize * ctx.FontScale, node.Bounds.Width, text.Trim).Text;
                if (shown != text.Value)
                {
                    cut.Add((text.Value, shown, new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height)));
                }
            }
        }

        return cut.ToImmutable();
    }

    /// <summary>Paints the screen and the viewer in its canvas.</summary>
    public void Render(WorkbenchState state, WorkbenchActions actions, ViewerState viewerState, IPreviewSource? source, float width, float height)
    {
        BeginFrame();
        _viewerState = viewerState;
        var palette = GuiTheme.Palette;
        FillRect(0, 0, width, height, palette.ContentBg);

        // A word wider than its flow is broken at the width the engine arranged the flow at (Flow): arranged once, and again in
        // the same frame only when a flow's width changed (the first frame, a resize, a new DPI), so a break is never a frame late.
        var bounds = new RectF32(0, 0, width, height);
        var onPages = state.Stage != WorkbenchStage.Workbench;
        _onPages = onPages;
        _designWidth = Scale.ToDesignX(width);
        var arranged = ArrangeLayout(onPages ? BuildNewProject(state, actions) : Build(state, actions), bounds);
        if (TookRooms(arranged))
        {
            arranged = ArrangeLayout(onPages ? BuildNewProject(state, actions) : Build(state, actions), bounds);
        }

        // The second build's flows are never looked up (the next frame's build fills the map afresh), so say they are done with.
        _flows.Clear();
        PaintLayout(arranged);
        if (!onPages)
        {
            DrawHoveredPlayMarks(arranged);
        }

        if (onPages)
        {
            // The pages of New project hold no canvas: the viewer is not drawn, and the only overlay is a file's kind menu.
            CanvasRect = default;
            RenderKindMenu(arranged, width, height);
            return;
        }

        CanvasRect = ArrangedFills.RectOf(arranged, CanvasKey);

        if (CanvasRect.Width > 0f && CanvasRect.Height > 0f)
        {
            if (state.View is null)
            {
                RenderLayout(Layout.Builder.VStack(
                        Layout.Builder.Text("Open a project, or make a new one from a capture folder, to see the steps of an image.", BaseFontSize,
                            palette.DimText, TextAlign.Center, TextAlign.Center, trim: TextTrim.End).WStar().HStar())
                    .Pad(Gap * 2).Bg(palette.ContentBg), CanvasRect);
            }
            else if (state.SelectedFrames is not null && state.PreviewFrame is null)
            {
                RenderLayout(Layout.Builder.VStack(
                        Layout.Builder.Text("Click a file in the list to see it here.", BaseFontSize, palette.DimText, TextAlign.Center, TextAlign.Center, trim: TextTrim.End)
                            .WStar().HStar())
                    .Pad(Gap * 2).Bg(palette.ContentBg), CanvasRect);
            }
            else if (actions.ShowsEmptyState)
            {
                RenderLayout(EmptyState(actions), CanvasRect);
            }
            else if (state.SelectedFrames is null && state.OriginalNote is { } note && actions.EffectiveView() is WorkbenchViewKind.Original)
            {
                RenderLayout(Layout.Builder.VStack(
                        Layout.Builder.Text(note, BaseFontSize, palette.DimText, TextAlign.Center, TextAlign.Center).WStar().HStar())
                    .Pad(Gap * 2).Bg(palette.PanelBg), CanvasRect);
            }
            else
            {
                _viewer.SetSurfaceSize((uint)Renderer.Width, (uint)Renderer.Height);
                _viewer.SetContentRegion(CanvasRect);
                // The texture upload a document swap asks for happens inside Render's PrepareFrame, before anything samples the
                // channel views.
                _viewer.Render(source, viewerState);
            }
        }

        RenderMenu(_imageMenu, ImageChoiceHit, arranged, width, height);
        RenderMenu(_stretchMenu, StretchChoiceHit, arranged, width, height);
        RenderMenu(_workflowMenu, WorkflowChoiceHit, arranged, width, height);
        RenderMenu(_addMenu, AddStepHit, arranged, width, height);
        foreach (var (key, menu) in _settingMenus.ToList())
        {
            RenderMenu(menu, SettingHitPrefix + key, arranged, width, height);
        }

        RenderKindMenu(arranged, width, height);
    }

    private Layout.Node Build(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var body = Layout.Builder.HStack(
                Tree(state, actions).WFixed(TreeWidth).HStar(),
                Panel(state, actions).WFixed(PanelWidth).HStar(),
                Layout.Builder.Fill(key: CanvasKey).WStar().HStar())
            .WStar().HStar();
        var rows = new List<Layout.Node> { TopBar(state, actions).WStar().HFixed(TopBarHeight) };
        rows.Add(Toolbar(state, actions).WStar().HFixed(ToolbarHeight));
        rows.Add(body);
        rows.Add(StatusStrip(state, actions).WStar());
        return Layout.Builder.VStack([.. rows]).Stretch().Bg(palette.ContentBg);
    }

    // The project, the image and Open project, on one row of one height and one middle.
    private Layout.Node TopBar(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var view = state.View;
        var parts = new List<Layout.Node>();
        var folder = view?.Folder ?? "No project open";
        var waits = actions.ChoiceWaits;
        // A refused New project is said in words in this row, in the place of the folder, whole text on hover: no band of its own, so
        // nothing is reserved for it and nothing moves when it comes.
        if (state.NewProjectRefusal is { } refusal)
        {
            parts.Add(Layout.Builder.Text(refusal, BaseFontSize, palette.Error, trim: TextTrim.End).WStar().HStar().WithTooltip(refusal));
        }
        else
        {
            parts.Add(Layout.Builder.Text(folder, BaseFontSize, view is null ? palette.DimText : palette.BodyText, trim: TextTrim.Start)
                .WStar().HStar().WithTooltip(folder));
        }
        if (view is { ImageName: { } name } && view.ImageNames.Length > 1)
        {
            parts.Add(ImageChoice(view, actions, waits));
        }
        else
        {
            _imageMenu.Close();
            if (view is { ImageName: { } only })
            {
                // A long image name (a capture folder's) ends in an ellipsis with the whole name on hover; it never pushes the buttons off the bar.
                parts.Add(Layout.Builder.Text(only, BaseFontSize, palette.BodyText, TextAlign.Far, trim: TextTrim.End)
                    .WStar().HStar().WithTooltip(only));
            }
        }

        // While a run goes it writes the store, and while a press of Process this master lays out its chain that press opens its
        // own result, so no other project or image is read: the controls stay where they are, dimmed, and say why on hover
        // (WorkbenchActions.ChoiceWaits; WorkbenchActions refuses them too).
        // With no project open these two are all there is to do, so they take the accent, the colour of an action.
        var kind = view is null ? Layout.ButtonKind.Accent : Layout.ButtonKind.Standard;
        var open = Layout.Builder.Button("Open project", kind, actions.ChooseProject, GuiTheme.Theme,
            tooltip: waits ?? "Open a workbench project folder");
        var fresh = Layout.Builder.Button("New project", kind, actions.NewProject, GuiTheme.Theme,
            tooltip: waits ?? "Make a project from a folder of captured frames: it holds one step, the stack");
        parts.Add(waits is not null ? fresh.Disabled(waits) : fresh);
        parts.Add(waits is not null ? open.Disabled(waits) : open);
        return Layout.Builder.HStack([.. parts]).WithGap(Gap).CrossCenter().Pad(Gap, 0f).Bg(palette.HeaderBg);
    }

    // Image names are the user's own and can be many and long, so the choice is the engine's dropdown: a box that shows the image on
    // show, ellipsised inside its share of the row with the whole name on hover, and a menu of every image to pick from directly.
    private Layout.Node ImageChoice(WorkbenchView view, WorkbenchActions actions, string? waits)
    {
        var names = view.ImageNames;
        var current = view.ImageName ?? string.Empty;
        var at = names.IndexOf(current);
        _imageMenu.Items = [.. names.Select(static name => DropdownItem.Text(name))];
        _imageMenu.OnSelect = item => actions.ChooseImage(item.Value);
        if (waits is not null)
        {
            _imageMenu.Close();
        }

        // A node's own hover text wins over its disabled reason, so while the choice waits the reason is the hover text, whole name first.
        var tooltip = waits is not null
            ? $"{current}. {waits}"
            : $"{current}: image {at + 1} of {names.Length}. Press to choose another";
        var box = Layout.Builder.DropdownBox(current, _imageMenu, GuiTheme.Theme, new HitResult.ButtonHit(ImageChoiceHit), tooltip: tooltip).WStar();
        return waits is not null ? box.Disabled(waits) : box;
    }

    // The selected step (#2553 section 3): its name with an on/off, what it does, its state in words with what fixes it, its settings as
    // controls, what it gives, what serves it, and the command line folded away.
    private Layout.Node Panel(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var rows = new List<Layout.Node>();
        if (actions.SelectedFrameGroup is { } frameGroup)
        {
            return FramesPanel(frameGroup, state, actions);
        }

        if (actions.Panel() is not { } panel)
        {
            rows.Add(Words(state.View is { Refusal: { } refusal } ? refusal : "Select a step to see what it does.", palette.DimText));
            return Column(rows);
        }

        var step = panel.Step;
        // The title wraps in the room it has: a sentence is never cut mid-word. Beside it the step's switch.
        // A checkbox stands in for the mockup's switch until DIR.Lib#156 (switch control) and DIR.Lib#330 (slider with a numeric field) land.
        var onStyle = CheckboxStyleNow();
        var waits = actions.Jobs.IsRunning ? "A run is going: the step can be switched when it ends" : null;
        var on = Layout.Builder.Checkbox("On", step.State is not StepState.Off, actions.SetStepEnabled, onStyle, BaseFontSize,
                new HitResult.ButtonHit(StepSwitchHit))
            .WithTooltip(waits ?? "Switch this step off; the steps after it take its input").WFixed(BaseFontSize * 4.5f);
        // The switch stands under the title, not beside it: a beside-switch takes a share of a narrow panel and was cut by its edge.
        rows.Add(Words(step.Label, palette.BodyText, FlowRoom.Panel, BaseFontSize * 1.15f));
        rows.Add(waits is null ? on : on.Disabled(waits));
        if (panel.Summary.Length > 0)
        {
            rows.Add(Words(panel.Summary, palette.DimText));
        }

        var stateInk = step.State is StepState.Refused or StepState.Blocked ? palette.Error : step.Changed ? palette.Warn : palette.BodyText;
        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text(step.Mark, BaseFontSize, stateInk).WAuto(),
                Words(step.Words, stateInk).WStar())
            .WithGap(4f).CrossCenter().WStar());
        if (actions.FixWords(step) is { Length: > 0 } fix)
        {
            rows.Add(Words(fix, palette.DimText));
        }

        if (step.Message is { Length: > 0 } message)
        {
            rows.Add(Words(message, step.State is StepState.Refused or StepState.Blocked ? palette.Error : palette.DimText));
        }

        // One accent per view: the run is the toolbar's Run to <step> and the row's ▶, so the panel has no run button of its own.
        AddJob(rows, actions);
        AddMasters(rows, state, actions, panel);
        if (actions.ComparisonDeclined)
        {
            // What the canvas cannot say: the comparison was asked for and not kept.
            rows.Add(Words("The comparison was not kept for want of memory: the result is shown alone. Close other programs, then choose Before and after again.",
                palette.DimText));
        }

        rows.Add(Heading("Settings"));
        if (panel.SettingsRefusal is { } settingsRefusal)
        {
            rows.Add(Words(settingsRefusal, palette.Error));
        }

        var controls = actions.SettingControls();
        if (controls.IsEmpty && panel.SettingsRefusal is null)
        {
            rows.Add(Words("This step has no settings to change.", palette.DimText));
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls)
        {
            rows.Add(SettingControl(control, state, actions, waits, live));
        }

        foreach (var stale in _settingMenus.Keys.Where(key => !live.Contains(key)).ToList())
        {
            _settingMenus.Remove(stale);
        }

        if (state.SettingRefusal is { } settingRefusal)
        {
            rows.Add(Words(settingRefusal, palette.Error));
        }

        rows.Add(Heading("Gives"));
        foreach (var (name, result) in actions.Gives())
        {
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(name, BaseFontSize, palette.BodyText, trim: TextTrim.End).WStar().WithTooltip(name),
                    Layout.Builder.Text(result, BaseFontSize, palette.DimText, TextAlign.Far, trim: TextTrim.End).WAuto())
                .WithGap(Gap).CrossCenter().WStar());
        }

        if (panel.ServedBy.Length > 0)
        {
            rows.Add(Heading("Served by"));
            rows.Add(Words(panel.ServedBy, palette.BodyText));
        }

        // The command line is folded away: the line can be a path wide, and what a person came to do is in the settings.
        var open = state.CommandLineOpen;
        rows.Add(Layout.Builder.Button(open ? "Command line ⌃" : "Command line ⌄", Layout.ButtonKind.Standard, actions.ToggleCommandLine, GuiTheme.Theme,
            tooltip: "The command that does this step from a terminal").WAuto());
        if (open)
        {
            if (panel.CommandLine.IsEmpty)
            {
                rows.Add(Words("There is no command line until the step can be planned.", palette.DimText));
            }
            else
            {
                rows.Add(CommandLineText(panel, palette.BodyText));
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Button("Copy", Layout.ButtonKind.Standard, actions.CopyCommandLine, GuiTheme.Theme,
                            tooltip: "Put the command line on the clipboard").WAuto(),
                        Layout.Builder.Text(state.CommandCopied ? "Copied to the clipboard" : string.Empty, BaseFontSize * 0.85f, palette.DimText)
                            .WStar())
                    .WithGap(Gap).CrossCenter().WStar());
            }
        }

        return Column(rows);
    }

    private static Layout.CheckboxStyle CheckboxStyleNow()
    {
        var palette = GuiTheme.Palette;
        return new Layout.CheckboxStyle(palette.ContentBg, palette.Accent, palette.DimText, GuiTheme.Hover(palette.PanelBg))
        {
            CheckedLabelColor = palette.BodyText,
            RowFill = palette.PanelBg,
            CheckedRowFill = palette.PanelBg,
            BoxSize = BaseFontSize * 0.9f,
        };
    }

    /// <summary>The hit of a setting's control is this followed by the setting's name (a dropdown's, by its key).</summary>
    internal const string SettingHitPrefix = "Setting:";

    /// <summary>The hit of the selected step's on/off (not a step row's hit, which begins with Step:).</summary>
    internal const string StepSwitchHit = "StepSwitch:On";

    private readonly Dictionary<string, DropdownMenuState<string>> _settingMenus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextInputState> _settingInputs = new(StringComparer.Ordinal);

    // One setting as the control its kind asks for (OperationSettingsForm): a checkbox for on/off, a dropdown for one of a few, a field for a
    // number or text, and the value in words for a list or a record, which is edited in the project file. What it does stands under it.
    private Layout.Node SettingControl(WorkbenchSettingControl control, WorkbenchState state, WorkbenchActions actions, string? waits, HashSet<string> live)
    {
        var palette = GuiTheme.Palette;
        var field = control.Field;
        var name = field.Name;
        var key = $"{state.View?.ImageName}:{state.SelectedStep}:{name}";
        live.Add(key);
        Layout.Node Held(Layout.Node node) => waits is null ? node : node.Disabled(waits);
        Layout.Node Said(Layout.Node edit)
            => control.Summary.Length == 0
                ? edit
                : Layout.Builder.VStack(edit, Words(control.Summary, palette.DimText)).WithGap(2f).WStar();
        Layout.Node Labelled(Layout.Node edit)
            => Said(Layout.Builder.VStack(Layout.Builder.Text(control.Label, BaseFontSize * 0.9f, palette.DimText, trim: TextTrim.End).WStar().WithTooltip(name), edit)
                .WithGap(4f).WStar());

        switch (field.Kind)
        {
            case OperationSettingKind.Bool:
            {
                // A checkbox stands in for the mockup's switch until DIR.Lib#156 (a switch control) lands.
                var isOn = string.Equals(control.Value, "true", StringComparison.OrdinalIgnoreCase);
                return Said(Held(Layout.Builder.Checkbox(control.Label, isOn, flag => actions.SetSetting(name, flag ? "true" : "false"), CheckboxStyleNow(),
                    BaseFontSize, new HitResult.ButtonHit(SettingHitPrefix + name)).WStar()));
            }

            case OperationSettingKind.Choice:
            {
                if (!_settingMenus.TryGetValue(key, out var menu))
                {
                    menu = new DropdownMenuState<string>();
                    _settingMenus[key] = menu;
                }

                menu.Items = [.. field.Choices.Select(static choice => DropdownItem.Text(choice))];
                menu.OnSelect = item => actions.SetSetting(name, item.Value);
                if (waits is not null)
                {
                    menu.Close();
                }

                return Labelled(Held(MenuBox(control.Value, palette.BodyText, menu, SettingHitPrefix + key, $"{control.Label}: {control.Value}. Press to choose another")));
            }

            case OperationSettingKind.Integer or OperationSettingKind.Number or OperationSettingKind.Text:
            {
                // A number is a field, saved with Enter, until DIR.Lib#330 (a slider with a numeric field) lands. A field that is not being
                // typed in follows the runner's answer, never what was typed and not saved.
                if (!_settingInputs.TryGetValue(key, out var input))
                {
                    input = new TextInputState { Placeholder = field.Default ?? "none" };
                    input.OnCommit = text =>
                    {
                        actions.SetSetting(name, text);
                        return System.Threading.Tasks.Task.CompletedTask;
                    };
                    _settingInputs[key] = input;
                }

                if (!input.IsActive && input.Text != control.Value)
                {
                    input.Text = control.Value;
                }

                return Labelled(Held(Layout.Builder.TextInput(input, BaseFontSize).WStar().HFixed(GuiTheme.Metrics.ButtonHeight)
                    .WithTooltip($"{control.Label}: press Enter to save. Empty is {(field.Nullable ? "left out" : "not allowed")}")));
            }

            default:
            {
                // MISSING: a list or record setting has no control yet (a list of entries with add and remove); until one exists it is
                // said in words, "none" when empty, never as raw JSON. It is edited in the project file.
                var empty = control.Value is "" or "[]" or "{}" or "null";
                var said = empty ? "none" : control.Value.Length > 0 && control.Value[0] is '[' or '{' ? "set in the project file" : control.Value;
                return Said(Layout.Builder.HStack(
                        Layout.Builder.Text(control.Label, BaseFontSize, palette.DimText).WAuto(),
                        Layout.Builder.Text(said, BaseFontSize, palette.BodyText, TextAlign.Far, trim: TextTrim.End).WStar().WithTooltip(control.Value))
                    .WithGap(Gap).CrossCenter().WStar());
            }
        }
    }

    // The job panel: the running step and how far it is (a bar, and the percentage in words, where the operation reports one), how
    // the last run ended, and every line the run said, newest last, in a scroll of its own.
    private void AddJob(List<Layout.Node> rows, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var job = actions.Jobs.State;
        if (!job.Running && job.Outcome is WorkbenchJobOutcome.None)
        {
            return;
        }

        rows.Add(Heading("Job"));
        var failed = !job.Running && job.Outcome is WorkbenchJobOutcome.Stopped or WorkbenchJobOutcome.Failed;
        rows.Add(Words(job.Words, failed ? palette.Error : palette.BodyText));
        if (job.Running && job.Fraction is { } fraction)
        {
            var done = (float)Math.Clamp(fraction, 0d, 1d);
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Fill().WStar(Math.Max(done, 0.001f)).HStar().Bg(palette.Accent),
                    Layout.Builder.Fill().WStar(Math.Max(1f - done, 0.001f)).HStar().Bg(palette.RaisedBg))
                .WStar().HFixed(BaseFontSize * 0.6f));
        }

    }

    // The stack step's masters, one row each as the runner answered them: the group's name (ellipsised inside its share of the row with
    // the whole name on hover) and Process this master, on one height and one middle. While a run goes, a read is in flight or a
    // press is still laying out its chain, every button is dimmed and says why on hover. A refusal of the last press is said beside.
    private void AddMasters(List<Layout.Node> rows, WorkbenchState state, WorkbenchActions actions, WorkbenchStepPanel panel)
    {
        if (!panel.ShowsMasters)
        {
            return;
        }

        var palette = GuiTheme.Palette;
        rows.Add(Heading("Masters"));
        if (panel.Masters.IsEmpty)
        {
            rows.Add(Words(panel.Step.State is StepState.Kept or StepState.Ran
                ? "The stack gave no masters."
                : "The masters come once the stack has run: one for each target and filter group.", palette.DimText));
        }

        var waits = actions.ProcessMasterWaits;
        foreach (var group in panel.Masters)
        {
            var name = group;
            var process = Layout.Builder.Button("Process this master", Layout.ButtonKind.Standard, () => actions.ProcessMaster(name), GuiTheme.Theme,
                tooltip: waits ?? $"Add {name}'s enhance as its chain of single steps, then open the result");
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(name, BaseFontSize, palette.BodyText, trim: TextTrim.End).WStar().WithTooltip(name),
                    (waits is null ? process : process.Disabled(waits)).WAuto())
                .WithGap(Gap).CrossCenter().WStar());
        }

        if (state.MasterRefusal is { } refusal)
        {
            rows.Add(Words(refusal, palette.Error));
        }
    }

    private Layout.Node Column(List<Layout.Node> rows)
        => Layout.Builder.VStack([.. rows]).Pad(Gap).WithGap(Gap).Bg(GuiTheme.Palette.PanelBg).WithScroll(_panelScroll);

    private static Layout.Node Heading(string text)
        => Layout.Builder.Text(text.ToUpperInvariant(), BaseFontSize * 0.8f, GuiTheme.Palette.DimText).WStar();

    // A sentence that wraps rather than clips (the engine's flow of words, as the viewer's panels do).
    private Layout.Node Words(string text, RGBAColor32 color, FlowRoom where = FlowRoom.Panel, float size = 0f)
        => Flow(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), color, tooltip: null, where, size);

    // The command as one flow of arguments, wrapped, never cut. An argument too long for the panel (a path) is broken into pieces
    // that each fit, so every character is on screen; Copy puts the unbroken line on the clipboard, and each piece's hover text
    // is its whole argument.
    private Layout.Node CommandLineText(WorkbenchStepPanel panel, RGBAColor32 color)
        => Flow([.. panel.CommandLine.Select(static raw => StepCommandLine.Display([raw]))], color, tooltip: static word => word, FlowRoom.Panel);

    // Where a flow of words stands: straight in the panel's column, or in the job's list of lines (narrower by its own padding
    // and scroll bar). Each has one width the engine arranges, read back by TookRooms.
    private enum FlowRoom { Panel, JobLines, Groups, Cards, Preview, Tree }

    // The width, in surface units, the last arrange gave each kind of flow; infinite until a frame has arranged one.
    private readonly float[] _rooms = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];

    // The flows this build made, by reference, so the arranged tree tells which node is which flow.
    private readonly Dictionary<Layout.Node, FlowRoom> _flows = new(ReferenceEqualityComparer.Instance);

    // Reads the width the engine arranged each kind of flow at. True when one differs from the width the build broke words
    // against, so the caller builds again with the arranged widths.
    private bool TookRooms(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        Span<float> seen = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        foreach (var node in arranged)
        {
            if (_flows.TryGetValue(node.Node, out var where))
            {
                seen[(int)where] = MathF.Min(seen[(int)where], node.Bounds.Width);
            }
        }

        _flows.Clear();
        var changed = false;
        for (var i = 0; i < _rooms.Length; i++)
        {
            if (!float.IsPositiveInfinity(seen[i]) && MathF.Abs(seen[i] - _rooms[i]) > 0.5f)
            {
                _rooms[i] = seen[i];
                changed = true;
            }
        }

        return changed;
    }

    // Words in one wrapping flow with no gap of the engine's own between leaves: the space between two words is the trailing
    // space of the first, so the pieces of one broken word sit edge to edge and read as the word, never as a space inside a path.
    private Layout.Node Flow(IReadOnlyList<string> words, RGBAColor32 color, Func<string, string>? tooltip, FlowRoom where, float size = 0f)
    {
        var fontSize = size > 0f ? size : BaseFontSize;
        var ctx = MeasureContext();
        var room = _rooms[(int)where];
        var nodes = new List<Layout.Node>();
        for (var w = 0; w < words.Count; w++)
        {
            var word = words[w];
            var text = w < words.Count - 1 ? word + " " : word;
            foreach (var piece in Pieces(text, room, ctx, fontSize))
            {
                var leaf = Layout.Builder.Text(piece, fontSize, color);
                nodes.Add(tooltip is null ? leaf : leaf.WithTooltip(tooltip(word)));
            }
        }

        var flow = Layout.Builder.WrapH([.. nodes]).WithGap(0f).WStar();
        _flows[flow] = where;
        return flow;
    }

    // The text in the longest runs the engine measures as fitting the room, each ending after a path separator where one lies in
    // its second half, so a path breaks at a folder where it can. A text that fits is one run.
    private static List<string> Pieces(string text, float room, Layout.IMeasureContext<float> ctx, float fontSize)
    {
        var pieces = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            if (Width(text[start..], ctx, fontSize) <= room)
            {
                pieces.Add(text[start..]);
                break;
            }

            // The longest prefix that fits, found by halving: widths grow with every character added.
            var (fits, over) = (start + 1, text.Length);
            while (over - fits > 1)
            {
                var mid = (fits + over) / 2;
                if (Width(text[start..mid], ctx, fontSize) <= room)
                {
                    fits = mid;
                }
                else
                {
                    over = mid;
                }
            }

            var half = start + ((fits - start) / 2);
            var cut = text.LastIndexOfAny(['\\', '/', '-', '_', '.'], fits - 1, fits - start);
            var end = cut >= half ? cut + 1 : fits;
            pieces.Add(text[start..end]);
            start = end;
        }

        return pieces;
    }

    private static float Width(string text, Layout.IMeasureContext<float> ctx, float fontSize)
        => Layout.Engine.Measure(Layout.Builder.Text(text, fontSize), new Layout.Size<float>(float.PositiveInfinity, float.PositiveInfinity), ctx).Width;
}
