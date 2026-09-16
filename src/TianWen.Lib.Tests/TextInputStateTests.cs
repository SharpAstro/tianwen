using DIR.Lib;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("UI")]
public class TextInputStateTests
{
    /// <summary>
    /// The window's one focus owner. DIR.Lib 10.0 made <c>TextInputState.Activate</c> and the
    /// <c>IsActive</c> setter internal, so a test says "this field is being edited" the way a host does.
    /// </summary>
    private readonly TextInputFocus _focus = new();

    /// <summary>
    /// A field seeded with <paramref name="text"/>, caret at the end, holding the keyboard.
    /// <para>
    /// Seeding and focusing are SEPARATE here, deliberately, and that is what <c>Activate(text)</c> could
    /// not express: <c>Focus(input, seed)</c> selects the seed, which is right for opening an editor on a
    /// value and wrong for these tests -- with the value selected, the first Backspace deletes all of it
    /// and every assertion below would be measuring the selection rather than the edit.
    /// </para>
    /// </summary>
    private TextInputState Editing(string text = "-37")
    {
        var input = new TextInputState { Text = text, CursorPos = text.Length };
        _focus.Focus(input);
        return input;
    }

    [Fact]
    public void SeedingAFieldPutsTheCursorAtTheEndAndTheKeyboardOnIt()
    {
        var input = Editing();

        input.Text.ShouldBe("-37");
        input.CursorPos.ShouldBe(3);
        input.IsActive.ShouldBeTrue();
        _focus.Current.ShouldBeSameAs(input, "the owner and the field's own flag say the same thing");
    }

    /// <summary>
    /// The other half of the split: opening an editor on an existing value SELECTS it, so the first
    /// keystroke replaces rather than appends. Two sites used to follow <c>Activate(text)</c> with their
    /// own <c>SelectAll</c> and the ones that did not simply behaved differently.
    /// </summary>
    [Fact]
    public void FocusingWithASeedSelectsIt()
    {
        var input = new TextInputState();
        _focus.Focus(input, "-37");

        input.HandleKey(TextInputKey.Backspace);
        input.Text.ShouldBeEmpty();
    }

    [Fact]
    public void BackspaceAtEndDeletesLastChar()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Backspace);

        input.Text.ShouldBe("-3");
        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void InsertTextAtEndAppendsAndAdvancesCursor()
    {
        var input = Editing();

        input.InsertText(".");

        input.Text.ShouldBe("-37.");
        input.CursorPos.ShouldBe(4);
    }

    [Fact]
    public void InsertTextAtMiddleInsertsAtCursor()
    {
        var input = Editing();
        input.CursorPos = 1; // between '-' and '3'

        input.InsertText("1");

        input.Text.ShouldBe("-137");
        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void BackspaceAtStartDoesNothing()
    {
        var input = Editing();
        input.CursorPos = 0;

        input.HandleKey(TextInputKey.Backspace);

        input.Text.ShouldBe("-37");
        input.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void LeftArrowMovesCursorBack()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Left);

        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void RightArrowAtEndDoesNothing()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Right);

        input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void HomeMovesToStart()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Home);

        input.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void EndMovesToEnd()
    {
        var input = Editing();
        input.CursorPos = 0;

        input.HandleKey(TextInputKey.End);

        input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void EnterSetsIsCommitted()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Enter);

        input.IsCommitted.ShouldBeTrue();
    }

    [Fact]
    public void EscapeSetsIsCancelled()
    {
        var input = Editing();

        input.HandleKey(TextInputKey.Escape);

        input.IsCancelled.ShouldBeTrue();
    }

    [Fact]
    public void FullEditingSequence_SeedBackspaceTypeDigit()
    {
        // Simulates: activate with "-37", backspace, type "8" → "-38"
        var input = Editing();

        input.CursorPos.ShouldBe(3);

        input.HandleKey(TextInputKey.Backspace);
        input.Text.ShouldBe("-3");
        input.CursorPos.ShouldBe(2);

        input.InsertText("8");
        input.Text.ShouldBe("-38");
        input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void DeleteAtMiddleRemovesCharAtCursor()
    {
        var input = Editing();
        input.CursorPos = 1; // cursor on '3'

        input.HandleKey(TextInputKey.Delete);

        input.Text.ShouldBe("-7");
        input.CursorPos.ShouldBe(1);
    }
}
