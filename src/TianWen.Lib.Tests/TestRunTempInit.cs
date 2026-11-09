using System.Runtime.CompilerServices;
using TianWen.Lib.Tests;

namespace TianWen.Lib.Tests.RunTempInit;

/// <summary>
/// The module initializer of the assembly this file is compiled into: the temp redirect (<see cref="TestRunTemp"/>) has to happen before
/// any test code reads the temp path. Linked into every test project that references TianWen.Lib.Tests; the type is file-scoped, so each
/// copy is its own and never a CS0436 clash with another assembly's, and the logic lives once, in <see cref="TestRunTemp"/>.
/// </summary>
file static class TestRunTempInit
{
    [ModuleInitializer]
    internal static void Run() => TestRunTemp.Redirect();
}
