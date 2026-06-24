// Namespace alias so layout call sites read as the qualified Layout.Node / Layout.Builder / Layout.Sizing
// form, matching TianWen.UI.Abstractions. DIR.Lib's layout engine + the Builder DSL live in the
// DIR.Lib.Layout namespace; aliasing it keeps the collision-prone barewords (Node, Content, Size<T>) out
// of scope while still reading as Layout.X.
global using Layout = DIR.Lib.Layout;
