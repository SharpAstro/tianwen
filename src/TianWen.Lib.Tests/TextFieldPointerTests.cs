using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A press on a text field lands the caret where it was aimed, a second click takes the WORD under it and a
/// third the whole field, on the CPU renderer and therefore on every pixel host.
/// </summary>
/// <remarks>
/// Driven through the pair the hosts use -- the widget that painted the field answers
/// <see cref="ICaretPlacingWidget.CaretIndexAt"/>, and
/// <see cref="TextFieldPointerInteraction.HandleMouseDown"/> decides what the click MEANS -- rather than
/// through a host's own dispatcher, so it pins the rule for the GUI and the web build at once. Before this,
/// each of them focused the field and left the caret at the END of the text, and a double click selected
/// the lot: what the test presses on is chosen so that BOTH of those answers are wrong here.
/// <para>
/// The press position is derived from the SAME measurement the paint used (through the widget's own
/// renderer and font), never from a pixel literal: a literal would pin nothing but whichever face this box
/// happens to have.
/// </para>
/// </remarks>
[Collection("UI")]
public class TextFieldPointerTests
{
    private const string Value = "alpha beta";
    private const int WordStart = 6;    // Value[6..10] is "beta".
    private const float FontSize = 16f;

    private const int FieldX = 40;
    private const int FieldY = 20;
    private const int FieldW = 300;
    private const int FieldH = 28;

    /// <summary>
    /// The smallest thing that can host a field: one <c>RenderTextInput</c> call, which is the same
    /// protected helper every production widget paints its fields with, so the hit carries the
    /// <see cref="TextInputGeometry"/> a real tab's would.
    /// </summary>
    private sealed class FieldWidget(RgbaImageRenderer renderer)
        : PixelWidgetBase<RgbaImage>(renderer), ICaretPlacingWidget
    {
        public void Render(TextInputState field)
        {
            BeginFrame();
            RenderTextInput(field, FieldX, FieldY, FieldW, FieldH, FontPath, FontSize);
        }

        /// <summary>Width of the first <paramref name="chars"/> characters of <see cref="Value"/>.</summary>
        public float PrefixWidth(int chars)
            => Renderer.MeasureText(Value.AsSpan(0, chars), FontPath, FontSize).Width;
    }

    private sealed class Harness : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly FieldWidget _widget;

        public Harness()
        {
            _renderer = new RgbaImageRenderer(640, 200);
            _widget = new FieldWidget(_renderer) { FontPath = FontResolver.ResolveSystemFont() };
            Field = new TextInputState { Text = Value };
            _widget.Render(Field);

            // Through the real hit test, so a geometry the paint failed to register shows up here rather
            // than as a caret quietly landing at zero.
            Hit = _widget.HitTest(FieldX + 5f, FieldY + FieldH / 2f).ShouldBeOfType<HitResult.TextInputHit>();
            Hit.Input.ShouldBeSameAs(Field);
        }

        public TextInputState Field { get; }

        public TextInputFocus Focus { get; } = new TextInputFocus();

        public HitResult.TextInputHit Hit { get; }

        /// <summary>A press at <paramref name="pointerX"/>, exactly as a host routes one.</summary>
        public void Press(float pointerX, int clicks, InputModifier modifiers = InputModifier.None)
            => TextFieldPointerInteraction.HandleMouseDown(
                Hit, _widget, pointerX, clicks, modifiers, Focus, () => { });

        /// <summary>
        /// A pointer in the left quarter of the glyph that starts at <paramref name="boundary"/> --
        /// unambiguously that boundary, since the caret goes to the NEARER one.
        /// </summary>
        public float XOfBoundary(int boundary)
        {
            var origin = TextInputRenderer.TextOriginX(FieldX, FontSize, 0f);
            var here = _widget.PrefixWidth(boundary);
            var next = _widget.PrefixWidth(boundary + 1);
            return origin + here + (next - here) * 0.25f;
        }

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public void OneClickPutsTheCaretUnderThePointer()
    {
        using var h = new Harness();

        h.Press(h.XOfBoundary(WordStart), clicks: 1);

        h.Focus.Current.ShouldBeSameAs(h.Field);
        h.Field.CursorPos.ShouldBe(WordStart);
        h.Field.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void TwoClicksSelectTheWordUnderThePointerAndNothingElse()
    {
        using var h = new Harness();

        h.Press(h.XOfBoundary(WordStart), clicks: 2);

        Value[h.Field.SelectionStart..h.Field.SelectionEnd].ShouldBe("beta");
        h.Field.SelectionStart.ShouldBe(WordStart);
        h.Field.SelectionEnd.ShouldBe(Value.Length);
    }

    [Fact]
    public void ThreeClicksSelectTheWholeField()
    {
        using var h = new Harness();

        h.Press(h.XOfBoundary(WordStart), clicks: 3);

        h.Field.SelectionStart.ShouldBe(0);
        h.Field.SelectionEnd.ShouldBe(Value.Length);
    }

    [Fact]
    public void ShiftExtendsFromWhereTheCaretAlreadyWas()
    {
        using var h = new Harness();

        h.Press(h.XOfBoundary(WordStart), clicks: 1);
        h.Press(FieldX + FieldW - 1f, clicks: 1, InputModifier.Shift);

        h.Field.SelectionStart.ShouldBe(WordStart);
        h.Field.SelectionEnd.ShouldBe(Value.Length);
    }
}
