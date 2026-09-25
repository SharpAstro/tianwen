using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The programs a client starts are built beside it (P1 of docs/plans/hardware-in-the-server.md, #917): a client
/// starts <c>tianwen-server</c> from its OWN directory and never from PATH, and the node looks for
/// <c>tianwen-ascomhost</c> beside itself. <c>src/ExeBeside.targets</c> makes that hold in a checkout; this project
/// imports it as the GUI and the CLI do, so its output is a client's.
/// </summary>
public class ExeBesideTests
{
    private static string Beside(string program) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? program + ".exe" : program);

    [Fact(Timeout = 30_000)]
    public async Task TheNodeServerBesideItsClientRunsFromThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = Beside("tianwen-server");
        File.Exists(server).ShouldBeTrue($"{server} is built beside the program that starts it");

        // A wrong argument ends it before it hosts anything or touches a device, and after it has loaded the node's
        // own assemblies (NodeArguments is in TianWen.Hosting), so an exit with the usage is the server resolving its
        // dependencies from HERE, not from its own build folder.
        using var process = Process.Start(new ProcessStartInfo(server, "--port not-a-port")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }).ShouldNotBeNull();
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        process.ExitCode.ShouldBe(NodeExitCodes.InvalidArguments, stderr);
        stderr.ShouldContain(NodeArguments.Usage);
    }

    [Fact]
    public void OnWindowsTheNodeBringsTheAscomHostWithIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "tianwen-ascomhost hosts Windows COM and is built on Windows only");

        File.Exists(Beside("tianwen-ascomhost")).ShouldBeTrue("the node looks for the ASCOM host beside itself, and the node is here");
    }
}
