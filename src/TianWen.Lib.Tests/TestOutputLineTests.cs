using System;
using System.Collections.Generic;
using System.Threading;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public class TestOutputLineTests
{
    [Fact]
    public void ALineWhileTheTestRunsGoesToItsOutput()
    {
        var output = new RecordingOutput();

        TestOutputLine.Write(output, "a line", TestContext.Current.CancellationToken).ShouldBeTrue();

        output.Lines.ShouldBe(["a line"]);
    }

    [Fact]
    public void AnOutputThatRefusesTheLineIsNeverThrownThrough()
    {
        // What xunit does to a write once the test has ended: "There is no currently active test".
        var output = new RecordingOutput { Refuses = true };

        TestOutputLine.Write(output, "too late").ShouldBeFalse();

        output.Attempts.ShouldBe(1);
    }

    [Fact]
    public void ACancelledTokenSendsTheLineAwayWithoutTryingTheOutput()
    {
        // A test that outran its Timeout: xunit cancels its token, then closes its output.
        var output = new RecordingOutput();

        TestOutputLine.Write(output, "after the timeout", new CancellationToken(canceled: true)).ShouldBeFalse();

        output.Attempts.ShouldBe(0);
    }

    [Fact]
    public void NoOutputAtAllIsNotAnError() => TestOutputLine.Write(null, "nobody to tell").ShouldBeFalse();

    private sealed class RecordingOutput : ITestOutputHelper
    {
        public bool Refuses { get; init; }

        public int Attempts { get; private set; }

        public List<string> Lines { get; } = [];

        public string Output => string.Join(Environment.NewLine, Lines);

        public void Write(string message) => WriteLine(message);

        public void Write(string format, params object[] args) => WriteLine(string.Format(format, args));

        public void WriteLine(string message)
        {
            Attempts++;
            if (Refuses)
            {
                throw new InvalidOperationException("There is no currently active test.");
            }
            Lines.Add(message);
        }

        public void WriteLine(string format, params object[] args) => WriteLine(string.Format(format, args));
    }
}
