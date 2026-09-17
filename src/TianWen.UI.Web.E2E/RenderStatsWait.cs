namespace TianWen.UI.Web.E2E;

/// <summary>
/// A <c>page.WaitForFunctionAsync</c> predicate over the <c>?e2e=1</c> render stats, which is how a probe waits on
/// something the app reports instead of on a sleep.
/// </summary>
/// <remarks>
/// <para><b>The predicate must be synchronous.</b> Every <c>window.__tianwenTest</c> method crosses into .NET and
/// returns a Promise, and Playwright does not await what a wait predicate returns: a Promise is an object, an
/// object is truthy, and the wait resolves on the first poll with nothing waited for. That is what the first
/// object-picture probe did (it "saw" zero pictures drawn and went on), and the Milky Way probe carried the same
/// predicate.</para>
/// <para>So the predicate starts one stats request at a time and tests the last snapshot that came back. The
/// fields it tests come from ONE snapshot, so a flag and a frame count can never be read from two different
/// frames. The hook is installed only once Blazor has booted; before that there is nothing to ask, which is
/// "not yet".</para>
/// </remarks>
internal static class RenderStatsWait
{
    /// <summary>
    /// The predicate: true-ish (the value of <paramref name="result"/>) once <paramref name="condition"/> holds for
    /// a stats snapshot bound to <c>s</c>, false before.
    /// </summary>
    /// <param name="condition">A JavaScript expression over <c>s</c>, e.g. <c>s.pictures &gt; 0</c>.</param>
    /// <param name="result">A JavaScript expression over <c>s</c> to resolve with; it must be truthy when the condition holds.</param>
    public static string Script(string condition, string result)
        => "() => { const t = window.__tianwenTest; if (!t) return false; "
            + "if (!window.__tianwenStatsInFlight) { window.__tianwenStatsInFlight = true; "
            + "t.getRenderStats().then(j => { window.__tianwenStats = JSON.parse(j); })"
            + ".finally(() => { window.__tianwenStatsInFlight = false; }); } "
            + "const s = window.__tianwenStats; "
            + $"return s && ({condition}) ? ({result}) : false; }}";
}
