using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="IEnhancerAvailability.Serves"/> is the one rule every gate on a role asks. A registration
/// is not an answer: <c>AddRcAstroAi()</c> registers a deferred star remover on every host, and where
/// StarXTerminator is absent its first use throws, so a gate that asked <c>is null</c> read it as
/// present and the run failed later, mid-way.
/// </summary>
public class EnhancerAvailabilityTests
{
    private sealed class CannotSay : IStarRemover
    {
        public string Name => nameof(CannotSay);

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    private sealed class ServesOnly(int channels) : IStarRemover, IEnhancerAvailability
    {
        public string Name => nameof(ServesOnly);

        public bool CanServe(int channelCount, EnhanceOptions options) => channelCount == channels;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    [Fact]
    public void NothingRegisteredServesNothing()
    {
        IEnhancerAvailability.Serves(null, 3, EnhanceOptions.Default).ShouldBeFalse();
        IEnhancerAvailability.ServesAny(null, EnhanceOptions.Default).ShouldBeFalse();
    }

    [Fact]
    public void AnEnhancerThatCannotSayServesWhateverItIsHanded()
    {
        var plain = new CannotSay();

        IEnhancerAvailability.Serves(plain, 1, EnhanceOptions.Default).ShouldBeTrue();
        IEnhancerAvailability.Serves(plain, 3, EnhanceOptions.Default).ShouldBeTrue();
    }

    [Fact]
    public void ARegisteredEnhancerThatDeclinesIsAbsent()
    {
        var declines = new ServesOnly(channels: 0);

        IEnhancerAvailability.Serves(declines, 3, EnhanceOptions.Default).ShouldBeFalse();
        IEnhancerAvailability.ServesAny(declines, EnhanceOptions.Default).ShouldBeFalse();
    }

    /// <summary>A gate asked before the input exists cannot know its shape, so it asks both a role is
    /// ever handed; one that serves only colour still serves the run.</summary>
    [Fact]
    public void ServesAnyAsksBothShapesARoleIsHanded()
    {
        var colourOnly = new ServesOnly(channels: 3);

        IEnhancerAvailability.Serves(colourOnly, 1, EnhanceOptions.Default).ShouldBeFalse();
        IEnhancerAvailability.ServesAny(colourOnly, EnhanceOptions.Default).ShouldBeTrue();
    }
}
