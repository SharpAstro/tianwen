using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TianWen.Lib.Tests")]
// The mirror parity harness (MirrorParityTests) polls a mirror at the instants it holds a session, not on the mirror's own
// cadence.
[assembly: InternalsVisibleTo("TianWen.Lib.Tests.Functional")]
