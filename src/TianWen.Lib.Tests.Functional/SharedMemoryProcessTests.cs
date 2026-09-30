using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A 26 MP frame from a node in another process, through shared memory and through the socket (P4b of
/// docs/plans/hardware-in-the-server.md, #932): the node, spawned as <c>tianwen-server</c> with a fake IMX571C, takes a
/// preview exposure, and this process fetches it both ways. Every frame through a slot is bit for bit the one the socket
/// carries. The times are printed for the record and never asserted on: a loaded machine is slower, not wrong.
/// </summary>
[Collection("NodeProcesses")]
public class SharedMemoryProcessTests(ITestOutputHelper output)
{
    private static readonly Guid ProfileId = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000932");
    private static readonly Uri Mount = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3");
    // Fake camera 3 is an IMX571C: 6248 x 4176, 26 MP.
    private static readonly Uri Camera = new Uri("Camera://FakeDevice/FakeCamera3");

    private static Task SeedProfileAsync(string dataRoot, CancellationToken ct)
    {
        var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
        var data = new ProfileData(
            Mount: Mount,
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [new OTAData("OTA 1", 1000, Camera: Camera, Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3);
        return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(ProfileId) + ".json"),
            JsonSerializer.Serialize(new ProfileDto(ProfileId, "Shared memory", data), Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task ATwentySixMegapixelFrameFromANodeInAnotherProcessComesThroughSharedMemoryBitExact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct));
        var client = kept.Client;
        (await client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await UntilTheJobSucceedsAsync(client, await client.StartDiscoveryAsync(ct), ct);
        await UntilTheJobSucceedsAsync(client, await client.ConnectDeviceAsync(Camera, ct), ct);

        using var slots = new FrameSlotReaders();
        var reader = new FrameReader();
        var lines = new List<string>();
        for (var exposure = 1; exposure <= 3; exposure++)
        {
            // A new frame each time, so every fetch through a slot has the node write one.
            var taken = (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct)).Value
                .ShouldNotBeNull("the node takes a preview");
            await UntilTheJobEndsAsync(client, taken.Id, ct);

            var viaSocket = Stopwatch.StartNew();
            var bytes = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, reader, ct);
            viaSocket.Stop();
            var viaSlot = Stopwatch.StartNew();
            var slotted = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, reader, slots, ct);
            viaSlot.Stop();

            var sent = bytes.Image.ShouldNotBeNull(bytes.Error);
            var read = slotted.Image.ShouldNotBeNull(slotted.Error);
            try
            {
                (read.Width, read.Height).ShouldBe((6248, 4176), "the IMX571C's whole frame");
                slotted.FrameNumber.ShouldBe(bytes.FrameNumber, "the same frame both ways");
                for (var c = 0; c < sent.ChannelCount; c++)
                {
                    read.GetChannelSpan(c).SequenceEqual(sent.GetChannelSpan(c)).ShouldBeTrue($"channel {c} through the slot is the frame the socket carried");
                }
                var megabytes = sent.Width * (double)sent.Height * sent.ChannelCount * (FrameWire.PacksAsUInt16(sent) ? 2 : 4) / 1e6;
                lines.Add($"exposure {exposure}: {megabytes:F0} MB, the socket {viaSocket.Elapsed.TotalMilliseconds:F0} ms, "
                    + $"shared memory {viaSlot.Elapsed.TotalMilliseconds:F0} ms (the node writing the slot, this process copying it out)");
            }
            finally
            {
                sent.Release();
                read.Release();
            }
        }

        slots.FramesRead.ShouldBe(3, "every frame asked for through shared memory came out of a slot");
        foreach (var line in lines)
        {
            output.WriteLine($"{Environment.OSVersion.Platform}, cross-process 26 MP: {line}");
        }
    }
}
