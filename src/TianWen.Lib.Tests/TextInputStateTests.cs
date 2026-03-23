using DIR.Lib;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public class TextInputStateTests
{
    [Fact]
    public void ActivateWithTextSetsCursorToEnd()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.Text.ShouldBe("-37");
        input.CursorPos.ShouldBe(3);
        input.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void BackspaceAtEndDeletesLastChar()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Backspace);

        input.Text.ShouldBe("-3");
        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void InsertTextAtEndAppendsAndAdvancesCursor()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.InsertText(".");

        input.Text.ShouldBe("-37.");
        input.CursorPos.ShouldBe(4);
    }

    [Fact]
    public void InsertTextAtMiddleInsertsAtCursor()
    {
        var input = new TextInputState();
        input.Activate("-37");
        input.CursorPos = 1; // between '-' and '3'

        input.InsertText("1");

        input.Text.ShouldBe("-137");
        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void BackspaceAtStartDoesNothing()
    {
        var input = new TextInputState();
        input.Activate("-37");
        input.CursorPos = 0;

        input.HandleKey(TextInputKey.Backspace);

        input.Text.ShouldBe("-37");
        input.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void LeftArrowMovesCursorBack()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Left);

        input.CursorPos.ShouldBe(2);
    }

    [Fact]
    public void RightArrowAtEndDoesNothing()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Right);

        input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void HomeMovesToStart()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Home);

        input.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void EndMovesToEnd()
    {
        var input = new TextInputState();
        input.Activate("-37");
        input.CursorPos = 0;

        input.HandleKey(TextInputKey.End);

        input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void EnterSetsIsCommitted()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Enter);

        input.IsCommitted.ShouldBeTrue();
    }

    [Fact]
    public void EscapeSetsIsCancelled()
    {
        var input = new TextInputState();
        input.Activate("-37");

        input.HandleKey(TextInputKey.Escape);

        input.IsCancelled.ShouldBeTrue();
    }

    [Fact]
    public void FullEditingSequence_ActivateBackspaceTypeDigit()
    {
        // Simulates: activate with "-37", backspace, type "8" → "-38"
        var input = new TextInputState();
        input.Activate("-37");

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
        var input = new TextInputState();
        input.Activate("-37");
        input.CursorPos = 1; // cursor on '3'

        input.HandleKey(TextInputKey.Delete);

        input.Text.ShouldBe("-7");
        input.CursorPos.ShouldBe(1);
    }
}
