// Alias rather than `using DIR.Lib.Layout;` -- the namespace's bareword types (Node, Content, Size<T>)
// collide readily, so the codebase writes the qualified Layout.Node / Layout.Builder instead. Matches
// TianWen.UI.Abstractions, TianWen.UI.Gui and TianWen.Lib.Tests.
global using Layout = DIR.Lib.Layout;
