using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
public class ExperienceReplayBufferTests
{
    private static OnlineGuideExperience MakeExperience(float targetRa, float targetDec)
    {
        return new OnlineGuideExperience
        {
            Features = new float[NeuralGuideModel.InputSize],
            TargetRa = targetRa,
            TargetDec = targetDec,
            PriorityWeight = 1.0f,
            OutcomeKnown = false
        };
    }

    [Fact]
    public void GivenEmptyBufferWhenSampleThenReturnsZero()
    {
        var buffer = new ExperienceReplayBuffer();
        Span<int> indices = stackalloc int[8];
        var count = buffer.SampleBatch(indices, new Random(42));
        count.ShouldBe(0);
    }

    [Fact]
    public void GivenBufferWhenAddThenCountIncreases()
    {
        var buffer = new ExperienceReplayBuffer();
        buffer.Count.ShouldBe(0);

        buffer.Add(MakeExperience(0.1f, 0.2f));
        buffer.Count.ShouldBe(1);
        buffer.TotalWritten.ShouldBe(1);

        buffer.Add(MakeExperience(0.3f, 0.4f));
        buffer.Count.ShouldBe(2);
    }

    [Fact]
    public void GivenBufferWhenOverflowThenWrapsAround()
    {
        var buffer = new ExperienceReplayBuffer(capacity: 4);

        for (var i = 0; i < 6; i++)
        {
            var exp = MakeExperience(i * 0.1f, 0);
            exp.OutcomeKnown = true;
            buffer.Add(exp);
        }

        buffer.Count.ShouldBe(4); // capped at capacity
        buffer.TotalWritten.ShouldBe(6);

        // The oldest entries should have been overwritten
        // Newest entries: indices 2,3,4,5 → wrapped to slots 2,3,0,1
        buffer.GetAt(0).TargetRa.ShouldBe(0.4f, 0.001f); // entry 4
        buffer.GetAt(1).TargetRa.ShouldBe(0.5f, 0.001f); // entry 5
    }

    [Fact]
    public void GivenBufferWhenOutcomeNotKnownThenSampleSkips()
    {
        var buffer = new ExperienceReplayBuffer();

        // Add 10 experiences, none with outcome known
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(MakeExperience(0.1f, 0.1f));
        }

        Span<int> indices = stackalloc int[5];
        var count = buffer.SampleBatch(indices, new Random(42));
        count.ShouldBe(0); // none have OutcomeKnown
    }

    [Fact]
    public void GivenBufferWhenOutcomeKnownThenSampleReturnsIndices()
    {
        var buffer = new ExperienceReplayBuffer();

        for (var i = 0; i < 10; i++)
        {
            var exp = MakeExperience(i * 0.1f, 0);
            exp.OutcomeKnown = true;
            buffer.Add(exp);
        }

        Span<int> indices = stackalloc int[5];
        var count = buffer.SampleBatch(indices, new Random(42));
        count.ShouldBe(5);

        for (var i = 0; i < count; i++)
        {
            indices[i].ShouldBeInRange(0, 9);
        }
    }

    [Fact]
    public void GivenBufferWhenUpdateOutcomeThenPriorityWeightSet()
    {
        var buffer = new ExperienceReplayBuffer();

        buffer.Add(MakeExperience(0.1f, 0.1f));
        buffer.GetAt(0).OutcomeKnown.ShouldBeFalse();

        // Error got worse: 1.0 → 2.0 (raRateScale=1.0 for simplicity)
        buffer.UpdateOutcome(nextRaError: 2.0, nextDecError: 0, prevRaError: 1.0, prevDecError: 0,
            raRateScale: 1.0, decRateScale: 1.0);

        buffer.GetAt(0).OutcomeKnown.ShouldBeTrue();
        buffer.GetAt(0).PriorityWeight.ShouldBeGreaterThan(1.0f); // upweighted because error grew
    }

    [Fact]
    public void GivenBufferWhenErrorImprovesThenPriorityWeightLow()
    {
        var buffer = new ExperienceReplayBuffer();

        buffer.Add(MakeExperience(0.1f, 0.1f));

        // Error improved: 2.0 → 0.5 (raRateScale=1.0 for simplicity)
        buffer.UpdateOutcome(nextRaError: 0.5, nextDecError: 0, prevRaError: 2.0, prevDecError: 0,
            raRateScale: 1.0, decRateScale: 1.0);

        buffer.GetAt(0).OutcomeKnown.ShouldBeTrue();
        buffer.GetAt(0).PriorityWeight.ShouldBeLessThan(1.0f); // downweighted because error shrank
    }

    [Fact]
    public void GivenBufferWhenUpdateOutcomeThenHindsightTargetComputed()
    {
        var buffer = new ExperienceReplayBuffer();

        // Applied correction of 0.5 normalized, initial P-controller target 0.3
        var exp = MakeExperience(0.3f, 0.1f);
        exp.AppliedRaNorm = 0.5f;
        exp.AppliedDecNorm = 0.2f;
        buffer.Add(exp);

        // Next frame still has 0.1px RA error → ideal correction was higher
        // ideal = applied - nextError * rateScale = 0.5 - 0.1 * 0.5 = 0.45
        buffer.UpdateOutcome(nextRaError: 0.1, nextDecError: -0.05, prevRaError: 1.0, prevDecError: 0.5,
            raRateScale: 0.5, decRateScale: 0.5);

        buffer.GetAt(0).OutcomeKnown.ShouldBeTrue();
        buffer.GetAt(0).TargetRa.ShouldBe(0.45f, 0.001f);  // hindsight-optimal, not the original 0.3
        buffer.GetAt(0).TargetDec.ShouldBe(0.225f, 0.001f); // 0.2 - (-0.05 * 0.5) = 0.225
    }

    [Fact]
    public void GivenBufferWhenResetThenEmpty()
    {
        var buffer = new ExperienceReplayBuffer();

        for (var i = 0; i < 5; i++)
        {
            buffer.Add(MakeExperience(0.1f, 0.1f));
        }

        buffer.Count.ShouldBe(5);

        buffer.Reset();
        buffer.Count.ShouldBe(0);
        buffer.TotalWritten.ShouldBe(0);
    }
}
