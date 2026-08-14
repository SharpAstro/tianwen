using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
// The web host reads the overlay gather counter for its E2E render-stats hook. The count is a
// diagnostic, not API: a cache miss draws the identical frame, so nothing else can tell a key that
// holds from one that misses per event, and the browser is the only place that runs the CPU overlay
// path at all. Kept internal rather than made public so it stays a diagnostic.
[assembly: InternalsVisibleTo("TianWen.UI.Web")]
