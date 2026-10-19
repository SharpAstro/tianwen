using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The sequence the Planetary mode's SEQUENCE section edits (#1340), before it is sent to the node: its steps (a filter, the
/// exposure and gain the panel had as the step was added, and a length in frames or seconds), its sets, and the planet its light
/// speaks for. Render thread only.
/// </summary>
public sealed class PlanetarySequenceDraft
{
    /// <summary>One step: the wheel's slot, the stream's settings, and how long it records, frames or seconds as <see cref="ByFrames"/> says.</summary>
    public sealed record Step(int FilterSlot, double ExposureMs, short Gain, bool ByFrames, int Frames, int Seconds);

    /// <summary>The frame targets a step steps through: the owner's 2026-10-07 Saturn took 10,000 a file.</summary>
    internal static readonly int[] FramePresets = [500, 1000, 2000, 5000, 10000, 20000];

    /// <summary>The time targets a step steps through, in seconds.</summary>
    internal static readonly int[] SecondPresets = [15, 30, 60, 90, 120, 180, 300];

    public ImmutableArray<Step> Steps { get; private set; } = [];

    /// <summary>How many times the steps are taken.</summary>
    public int Sets { get; private set; } = 1;

    /// <summary>The planet the light speaks for, chosen; null for the one the node found the mount pointing at.</summary>
    public CatalogIndex? Planet { get; set; }

    /// <summary>Adds a step through the slot after the last step's, at the panel's exposure and gain, 10,000 frames long.</summary>
    public void Add(int slots, double exposureMs, short gain)
    {
        var slot = Steps.IsEmpty || slots <= 0 ? 0 : (Steps[^1].FilterSlot + 1) % slots;
        Steps = Steps.Add(new Step(slot, exposureMs, gain, ByFrames: true, Frames: 10000, Seconds: 60));
    }

    public void Remove(int index)
    {
        if (index >= 0 && index < Steps.Length)
        {
            Steps = Steps.RemoveAt(index);
        }
    }

    /// <summary>Turns the step to the next of the wheel's slots.</summary>
    public void NextFilter(int index, int slots)
    {
        if (index >= 0 && index < Steps.Length && slots > 0)
        {
            Steps = Steps.SetItem(index, Steps[index] with { FilterSlot = (Steps[index].FilterSlot + 1) % slots });
        }
    }

    /// <summary>Switches the step between a frame target and a time target (#1340, the owner, 2026-10-19).</summary>
    public void ToggleLength(int index)
    {
        if (index >= 0 && index < Steps.Length)
        {
            Steps = Steps.SetItem(index, Steps[index] with { ByFrames = !Steps[index].ByFrames });
        }
    }

    /// <summary>Steps the step's length one preset up (<paramref name="by"/> 1) or down (-1), in its own unit.</summary>
    public void StepLength(int index, int by)
    {
        if (index < 0 || index >= Steps.Length)
        {
            return;
        }
        var step = Steps[index];
        Steps = Steps.SetItem(index, step.ByFrames
            ? step with { Frames = Neighbour(FramePresets, step.Frames, by) }
            : step with { Seconds = Neighbour(SecondPresets, step.Seconds, by) });
    }

    public void StepSets(int by) => Sets = Math.Clamp(Sets + by, 1, 99);

    /// <summary>How long a step lasts: its time, or its frames at <paramref name="framesPerSecond"/>; null when that cannot be said.</summary>
    public static TimeSpan? DurationOf(Step step, double framesPerSecond)
        => step.ByFrames
            ? framesPerSecond > 0 ? TimeSpan.FromSeconds(step.Frames / framesPerSecond) : null
            : TimeSpan.FromSeconds(step.Seconds);

    /// <summary>The sequence as the node takes it.</summary>
    public PlanetarySequenceRequestDto ToRequest() => new PlanetarySequenceRequestDto
    {
        Steps = [.. Steps.Select(static step => new PlanetarySequenceStepDto
        {
            FilterPosition = step.FilterSlot,
            ExposureMs = step.ExposureMs,
            Gain = step.Gain,
            Frames = step.ByFrames ? step.Frames : null,
            DurationSeconds = step.ByFrames ? null : step.Seconds,
        })],
        Sets = Sets,
    };

    private static int Neighbour(int[] presets, int value, int by)
    {
        var at = Array.IndexOf(presets, value);
        if (at < 0)
        {
            at = 0;
            while (at < presets.Length - 1 && presets[at] < value)
            {
                at++;
            }
        }
        return presets[Math.Clamp(at + by, 0, presets.Length - 1)];
    }
}
