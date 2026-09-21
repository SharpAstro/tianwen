using System.Runtime.CompilerServices;

// The drivers here are internal for the same reason they were internal inside TianWen.Lib: what a
// consumer registers is AddZWO() / AddQHY(), and the concrete driver types are an implementation
// detail of that registration. The same three test projects TianWen.Lib admits are admitted here,
// so a test reaches a driver exactly as it did before the split; today only TianWen.Lib.Tests does.
[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
[assembly: InternalsVisibleTo("TianWen.Lib.Tests.Functional")]
[assembly: InternalsVisibleTo("TianWen.Lib.Tests.Simulators")]
