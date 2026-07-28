using System.Runtime.CompilerServices;

// The TUI chrome is internal, but its geometry is worth pinning: the tab bar's click regions ARE the rects
// its labels are drawn into, and that is only assertable from a test that can arrange the tree.
[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
