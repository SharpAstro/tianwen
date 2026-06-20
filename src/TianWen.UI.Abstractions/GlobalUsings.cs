// Namespace alias so layout call sites read as the qualified Layout.Node / Layout.Builder / Layout.Sizing
// form. DIR.Lib's layout engine + the Builder DSL live in the DIR.Lib.Layout namespace; aliasing it keeps the
// call sites qualified without importing the namespace's collision-prone barewords (Node, Content, Size<T>).
global using Layout = DIR.Lib.Layout;
