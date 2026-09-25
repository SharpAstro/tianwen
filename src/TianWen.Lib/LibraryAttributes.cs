using System.Runtime.CompilerServices;

// The ZWO and QHYCCD drivers live in their own assembly so that referencing TianWen.Lib does
// not drag two vendors' native driver binaries along (see TianWen.Devices.Native.csproj).
// They stay internal and see the DAL device abstraction through here, rather than that
// abstraction being promoted to public API for a packaging split.
[assembly: InternalsVisibleTo("TianWen.Devices.Native")]
// The node's socket lives under the per-user AppData root (SharedStaticData.CommonDataRoot), and its ONE
// path helper sits in the contracts the node and every client share, so the root has one definition.
[assembly: InternalsVisibleTo("TianWen.Hosting.Contracts")]
[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
[assembly: InternalsVisibleTo("TianWen.Lib.Tests.Functional")]
[assembly: InternalsVisibleTo("TianWen.Lib.Tests.Simulators")]
[assembly: InternalsVisibleTo("TianWen.UI.Abstractions")]
[assembly: InternalsVisibleTo("TianWen.UI.Benchmarks")]
[assembly: InternalsVisibleTo("TianWen.UI.Gui")]
[assembly: InternalsVisibleTo("TianWen.UI.Shared")]
[assembly: InternalsVisibleTo("PrecomputeHdHipCross")]
[assembly: InternalsVisibleTo("PrecomputeSimbadMerge")]
[assembly: InternalsVisibleTo("BakeComets")]
[assembly: InternalsVisibleTo("BakeObjectImagery")]