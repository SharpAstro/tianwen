using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Profile discoveries overlap (the node's mount limit watcher looks every 5 s while the node writes profiles and clients
/// ask), and the one that started first can finish last. Its listing, taken before a profile was saved, must not replace the
/// listing that has it (#2000: an active profile vanished from the node's registry and the Alpaca plane answered "No telescope
/// with device number 0").
/// </summary>
public class ProfileIteratorOrderTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact(Timeout = 30_000)]
    public async Task ADiscoveryThatStartedFirstButFinishesLastDoesNotHideAProfileFoundSince()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, _folders.Create("profile-order-"));
        var iterator = new ProfileIterator(external, NullLogger<ProfileIterator>.Instance);

        // The first discovery lists the empty folder, then is held before it publishes.
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        iterator.AfterListing = async token =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0)
            {
                listed.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var stale = iterator.DiscoverAsync(ct).AsTask();
        await listed.Task.WaitAsync(ct);

        // A profile is saved and a later discovery finds it.
        var profile = new Profile(Guid.NewGuid(), "Saved meanwhile", ProfileData.Empty);
        await profile.SaveAsync(external, ct);
        await iterator.DiscoverAsync(ct);
        iterator.RegisteredDevices(DeviceType.Profile).OfType<Profile>().ShouldContain(p => p.ProfileId == profile.ProfileId);

        // The first one finishes last: what it listed is older, so it is not published.
        release.TrySetResult();
        await stale;

        iterator.RegisteredDevices(DeviceType.Profile).OfType<Profile>()
            .ShouldContain(p => p.ProfileId == profile.ProfileId, "an older listing does not replace a newer one");
    }
}
