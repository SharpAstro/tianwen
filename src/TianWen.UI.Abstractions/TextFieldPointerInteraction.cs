using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A widget that can say which character of a field IT painted a pointer is over.
/// </summary>
/// <remarks>
/// Satisfied for free by every <see cref="PixelWidgetBase{TSurface}"/> -- its
/// <c>CaretIndexAt</c> already has exactly this shape -- so a widget opts in by naming the interface and
/// writing no code at all.
/// <para>
/// It exists because DIR.Lib states that method on the generic base rather than on
/// <see cref="IPixelWidget"/>, and the GUI's press router (<see cref="GuiEventHandlerBase"/>) is
/// deliberately not generic over the surface: it holds an <see cref="IPixelWidget"/> and so has no way to
/// name the type the method is declared on. Asking the widget that PRODUCED the hit, rather than asking
/// whichever widget the router happens to hold, is the point -- the answer has to come from the renderer
/// and fallback chain the field was drawn with, or the caret lands somewhere other than the pointer.
/// </para>
/// </remarks>
public interface ICaretPlacingWidget
{
    /// <summary>
    /// Which character of <paramref name="hit"/>'s field a pointer at <paramref name="pointerX"/> is over,
    /// in the space the clickable regions were registered in.
    /// </summary>
    int CaretIndexAt(HitResult.TextInputHit hit, float pointerX);
}

/// <summary>
/// What a mouse press on a text field does, for every pixel host: give the field the keyboard, then place
/// the caret under the pointer, select the word under a second click and the whole field under a third,
/// with Shift extending from where the caret already was.
/// <para>
/// The rule itself is DIR.Lib's (<see cref="TextInputInteraction.HandlePointer"/>); what lives here is the
/// half that is tianwen's -- which widget to ask for the character index, and what to do when it cannot
/// answer. Shared by the SDL GUI (through <see cref="GuiEventHandlerBase"/>) and the Blazor/WebGL host the
/// way <see cref="PlannerSliderInteraction"/> is, because the two hosts previously carried the same four
/// hand-written lines and both of them were wrong the same way: a press focused the field and left the
/// caret at the END, and a double click selected the lot instead of the word under it.
/// </para>
/// </summary>
public static class TextFieldPointerInteraction
{
    /// <summary>
    /// Routes a press that landed on <paramref name="hit"/> to the caret. Returns true, always: a press on a
    /// field is consumed by the field.
    /// </summary>
    /// <param name="hit">The text-input hit the host's hit test produced.</param>
    /// <param name="painter">
    /// The widget whose hit test produced <paramref name="hit"/>, when it can place a caret. A host that
    /// knows its own surface type passes its widget directly; one that does not passes whatever it holds and
    /// lets the type test decide.
    /// <para>
    /// Null, or a widget that cannot answer, falls back to the end of the text -- which is where every host
    /// put the caret before this existed, so the worst case is the old behaviour rather than a caret at zero
    /// in a field the user has just clicked into the middle of.
    /// </para>
    /// </param>
    /// <param name="pointerX">The press position, in the space the regions were registered in.</param>
    /// <param name="clicks">Clicks in this run, as the platform counts them (SDL <c>clicks</c>, DOM <c>detail</c>).</param>
    /// <param name="modifiers">Shift here means "extend the selection to the caret", as it does everywhere else.</param>
    /// <param name="focus">Who has the keyboard; the field is focused through it, never by hand.</param>
    /// <param name="requestRedraw">Marks the surface dirty once the caret or the selection moved.</param>
    public static bool HandleMouseDown(
        HitResult.TextInputHit hit,
        IPixelWidget? painter,
        float pointerX,
        int clicks,
        InputModifier modifiers,
        TextInputFocus focus,
        Action requestRedraw)
    {
        var caretIndex = painter is ICaretPlacingWidget placer
            ? placer.CaretIndexAt(hit, pointerX)
            : hit.Input.Text.Length;

        return TextInputInteraction.HandlePointer(
            hit.Input, caretIndex, clicks,
            extend: (modifiers & InputModifier.Shift) != 0,
            new TextInputInteraction.PointerContext(focus, requestRedraw));
    }
}
