using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Console.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The CLI's rig verbs are this computer's node's clients (P6 of docs/plans/hardware-in-the-server.md, #936): a profile is
/// read from and written through the node's one profile writer, the devices are the node's listing, and a dark library and
/// a flat run are the node's runs, followed from here and stopped there. The real verbs and the real <see cref="ConsoleHost"/>
/// over a real node on its socket.
/// </summary>
[Collection("NodeProcesses")]
public class CliVerbsThroughTheNodeTests(ITestOutputHelper output)
{
    private sealed class Cli : IAsyncDisposable
    {
        private Cli(NodeHarness node, ConsoleHost host, StringWriter written, StringWriter errors)
        {
            Node = node;
            Host = host;
            Written = written;
            Errors = errors;
            Client = new TianWenNodeClient(node.Client);
        }

        public NodeHarness Node { get; }
        public ConsoleHost Host { get; }
        public StringWriter Written { get; }
        public StringWriter Errors { get; }

        /// <summary>Another client of the same node, for what the verbs do not say.</summary>
        public TianWenNodeClient Client { get; }

        public IDeviceHub Hub => Node.App.Services.GetRequiredService<IDeviceHub>();

        public static async Task<Cli> StartAsync(ITestOutputHelper output, CancellationToken ct)
        {
            var socket = Path.Combine(Directory.CreateTempSubdirectory("twcli").FullName, "node.sock");
            var node = await NodeHarness.StartAsync(output, ct, socketPath: socket);
            node.Factory.OnCreated = static controlled => RemoteSessionMirrorTests.Observing(controlled.Session);
            node.Factory.Initialised.TrySetResult();
            var written = new StringWriter();
            var errors = new StringWriter();
            var host = new ConsoleHost(node.External, Substitute.For<IHostApplicationLifetime>(), Substitute.For<IVirtualTerminal>(),
                new SystemTimeProvider(), NullLogger<ConsoleHost>.Instance,
                new LocalNodeOptions { NamedSocket = socket, AnotherAccountProbe = null }, written, errors);
            return new Cli(node, host, written, errors);
        }

        /// <summary>The verbs as <c>tianwen</c> composes them, run on <paramref name="args"/>.</summary>
        public Task<int> RunAsync(CancellationToken ct, params string[] args)
        {
            var active = new Option<string?>("--active", "-a") { Recursive = true };
            var selector = new ProfileSelector(Host, active);
            var root = new RootCommand
            {
                Options = { active },
                Subcommands =
                {
                    new ProfileSubCommand(Host, active, selector).Build(),
                    new DeviceSubCommand(Host).Build(),
                    new DarksSubCommand(Host).Build(),
                    new FlatsSubCommand(Host, selector).Build(),
                },
            };
            return root.Parse(args).InvokeAsync(cancellationToken: ct);
        }

        public async Task<Profile> ProfileNamedAsync(string name, CancellationToken ct)
        {
            var listed = (await Client.GetProfilesAsync(ct)).Value.ShouldNotBeNull();
            var id = listed.ShouldHaveSingleItem().ProfileId;
            var detail = (await Client.GetProfileAsync(id, ct)).Value.ShouldNotBeNull();
            detail.Name.ShouldBe(name);
            return new Profile(detail.ProfileId, detail.Name, detail.Data.ShouldNotBeNull());
        }

        public async ValueTask DisposeAsync()
        {
            Host.Dispose();
            await Node.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AProfileIsCreatedAndEditedThroughTheNodesOneWriter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);

        (await cli.RunAsync(ct, "profile", "create", "CLI rig")).ShouldBe(0, cli.Errors.ToString());
        var created = await cli.ProfileNamedAsync("CLI rig", ct);
        cli.Written.ToString().ShouldContain($"Created new profile 'CLI rig' with ID {created.ProfileId}", Case.Sensitive,
            "the id is the one the node gave it");

        (await cli.RunAsync(ct, "profile", "set-mount", "--active", "CLI rig", "FakeMount1")).ShouldBe(0, cli.Errors.ToString());
        (await cli.RunAsync(ct, "profile", "set-site", "--active", "CLI rig", "--lat", "48.2", "--lon", "16.3")).ShouldBe(0, cli.Errors.ToString());

        cli.Errors.ToString().ShouldBeEmpty();
        var edited = (await cli.ProfileNamedAsync("CLI rig", ct)).Data.ShouldNotBeNull();
        DeviceBase.SameDevice(edited.Mount, new FakeDevice(DeviceType.Mount, 1).DeviceUri).ShouldBeTrue($"the mount: {edited.Mount}");
        (edited.SiteLatitude, edited.SiteLongitude).ShouldBe((48.2, 16.3));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEditMadeAgainstAProfileThatMovedOnKeepsBothChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);
        var stored = (await cli.Host.SaveProfileAsync(new Profile(Guid.NewGuid(), "Rig", ProfileData.Empty), ct)).ShouldNotBeNull();
        var read = (await cli.Host.ListDevicesAsync<Profile>(DeviceType.Profile, DeviceDiscoveryOption.None, ct)).ShouldHaveSingleItem();

        // Another client changes the site after this one read the profile.
        var theirs = (await cli.Client.GetProfileAsync(stored.ProfileId, ct)).Value.ShouldNotBeNull();
        (await cli.Client.UpdateProfileAsync(stored.ProfileId, EquipmentActions.SetSite(theirs.Data.ShouldNotBeNull(), 48.2, 16.3, null),
            theirs.Revision.ShouldNotBeNull(), name: null, ct)).IsSuccess.ShouldBeTrue();

        var camera = new FakeDevice(DeviceType.Camera, 1).DeviceUri;
        var saved = await cli.Host.SaveProfileAsync(read.WithData((read.Data ?? ProfileData.Empty) with
        {
            OTAs = [new OTAData("Scope", 500, camera, null, null, null, null, null)],
        }), ct);

        saved.ShouldNotBeNull(cli.Errors.ToString());
        var now = await cli.ProfileNamedAsync("Rig", ct);
        var data = now.Data.ShouldNotBeNull();
        data.OTAs.ShouldHaveSingleItem().Camera.ShouldBe(camera, "this client's change");
        data.SiteLatitude.ShouldBe(48.2, "the other client's, which the edit was made again onto");
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeleteGoesToTheNodeWhichRefusesItsActiveProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);
        var active = (await cli.Host.SaveProfileAsync(new Profile(Guid.NewGuid(), "Active rig", ProfileData.Empty), ct)).ShouldNotBeNull();
        (await cli.Host.SaveProfileAsync(new Profile(Guid.NewGuid(), "Spare rig", ProfileData.Empty), ct)).ShouldNotBeNull();
        await cli.Node.Node.SetActiveProfileAsync(active.ProfileId, ct);

        await cli.RunAsync(ct, "profile", "delete", "Active rig");
        (await cli.RunAsync(ct, "profile", "delete", "Spare rig")).ShouldBe(0);

        cli.Errors.ToString().ShouldContain("The node did not delete the profile");
        var left = (await cli.Client.GetProfilesAsync(ct)).Value.ShouldNotBeNull();
        left.ShouldHaveSingleItem().Name.ShouldBe("Active rig");
        cli.Written.ToString().ShouldContain("Deleted profile 'Spare rig'");
    }

    private static bool IsFake(DeviceBase device) => string.Equals(device.DeviceClass, nameof(FakeDevice), StringComparison.OrdinalIgnoreCase);

    [Fact(Timeout = 60_000)]
    public async Task TheDevicesAreTheNodesListing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);

        var withFakes = await cli.Host.ListAllDevicesAsync(DeviceDiscoveryOption.IncludeFake, ct);
        var real = await cli.Host.ListAllDevicesAsync(DeviceDiscoveryOption.None, ct);

        withFakes.ShouldContain(static d => d.DeviceType == DeviceType.Camera && IsFake(d), "the node lists the fake rig");
        real.ShouldNotContain(static d => IsFake(d), "a fake is shown only when asked for");
        withFakes.ShouldNotContain(static d => d.DeviceType == DeviceType.Profile, "a profile is not a device to list");

        // Discovery is the node's job: run before the first listing, and again whenever the verb asks for one.
        async Task<int> DiscoveriesAsync() => (await cli.Client.GetJobsAsync(ct)).Value.ShouldNotBeNull()
            .Count(static j => j.Kind == "discover" && j.State == Hosting.Dto.JobState.Succeeded);
        var before = await DiscoveriesAsync();
        before.ShouldBeGreaterThan(0, "the first listing waited for the node to discover");
        (await cli.RunAsync(ct, "device", "discover")).ShouldBe(0, cli.Errors.ToString());
        (await DiscoveriesAsync()).ShouldBeGreaterThan(before, "`device discover` runs the node's discovery");
    }

    [Fact(Timeout = 90_000)]
    public async Task ADarkLibraryIsTheNodesRunAndTheCameraItConnectedIsGivenBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);
        var camera = (await cli.Host.ListAllDevicesAsync(DeviceDiscoveryOption.IncludeFake, ct))
            .First(static d => d.DeviceType == DeviceType.Camera);
        cli.Hub.IsConnected(camera.DeviceUri).ShouldBeFalse("premise: the node has not connected it");

        var exit = await cli.RunAsync(ct, "darks", "--exposure", "0.05", "--count", "2", "--camera", camera.DisplayName);

        exit.ShouldBe(0, cli.Errors.ToString());
        cli.Written.ToString().ShouldContain("[dark] 2 frame(s), sensor");
        cli.Hub.IsConnected(camera.DeviceUri).ShouldBeFalse("what the verb connected, it disconnected");
        var library = (await cli.Client.GetDarkLibraryAsync(ct)).Value.ShouldNotBeNull();
        library.Frames.Length.ShouldBe(2, "the node's run took them");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheFlatsVerbFollowsTheNodesRunToItsOwnEndAndSaysHowItEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);
        (await cli.Host.SaveProfileAsync(new Profile(Guid.NewGuid(), "Flat rig", ProfileData.Empty), ct)).ShouldNotBeNull();

        var verb = cli.RunAsync(ct, "flats", "--active", "Flat rig");
        while (cli.Node.Factory.Created.IsEmpty && !verb.IsCompleted)
        {
            await Task.Delay(20, ct);
        }
        var run = cli.Node.Factory.Created.ShouldHaveSingleItem($"the flat run never reached the node: {cli.Errors}");
        // Once the verb has read the run going on, the run ends: the verb must have kept following it to see how.
        bool SawAPhase() => cli.Written.ToString().Split('\n', StringSplitOptions.TrimEntries)
            .Any(static line => Enum.GetNames<SessionPhase>().Any(phase => line == $"[flats] {phase}"));
        while (!SawAPhase() && !verb.IsCompleted)
        {
            await Task.Delay(20, ct);
        }
        SawAPhase().ShouldBeTrue("premise: the verb read the run going on");
        run.Session.Phase.Returns(SessionPhase.Complete);
        run.EndsOnItsOwn.TrySetResult();
        run.Finalise.TrySetResult();

        (await verb).ShouldBe(0, $"{cli.Written}{cli.Errors}");
        cli.Written.ToString().ShouldContain("[flats] complete:");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFlatRunIsTheNodesRunWithTheCommandsKnobsAndACtrlCStopsItThere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cli = await Cli.StartAsync(output, ct);
        (await cli.Host.SaveProfileAsync(new Profile(Guid.NewGuid(), "Flat rig",
            EquipmentActions.SetSite(ProfileData.Empty, 48.2, 16.3, null)), ct)).ShouldNotBeNull();
        using var ctrlC = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var verb = cli.RunAsync(ctrlC.Token, "flats", "--active", "Flat rig", "--count", "3", "--brightness", "40");
        while (cli.Node.Factory.Created.IsEmpty && !verb.IsCompleted)
        {
            await Task.Delay(20, ct);
        }
        cli.Node.Factory.Created.IsEmpty.ShouldBeFalse($"the flat run never reached the node: {cli.Errors}");
        var run = cli.Node.Factory.Created.Last();
        await run.Started.Task.WaitAsync(ct);

        run.Configuration.FlatsPerFilter.ShouldBe(3);
        run.Configuration.FlatCalibratorBrightnessPercent.ShouldBe(40);
        run.Configuration.UnattendedPromptResponse.ShouldBe(UnattendedPromptResponse.Proceed,
            "an operator typed the command, and there is nobody else to ask");
        run.Configuration.SiteLatitude.ShouldBe(48.2, 1e-9);

        await ctrlC.CancelAsync();
        await run.Cancelled.Task.WaitAsync(ct);
        run.Finalise.TrySetResult();
        try
        {
            await verb;
        }
        catch (OperationCanceledException)
        {
            // How a cancelled command ends is the command line's business; that the node's run was stopped is this test's.
        }
        cli.Written.ToString().ShouldContain("stopping the flat run on the node");
    }
}
