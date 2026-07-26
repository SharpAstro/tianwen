using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto
{
    /// <summary>
    /// A full-fidelity <see cref="ScheduledObservation"/> on the wire, for pushing a locally-computed
    /// schedule to a node.
    /// <para>
    /// <b>Why <see cref="PendingTarget"/> is not enough.</b> That DTO carries name / RA / Dec / duration
    /// / gain, and <c>/session/start</c> stamps <c>Start = now</c> on whatever it drains. A schedule
    /// computed by the planner is much richer: a per-filter exposure plan, an <b>altitude-optimised
    /// start time</b> per target, and the <c>AcrossMeridian</c> flag. Driving a night through
    /// <c>PendingTarget</c> silently discards all of that -- every target would begin immediately, in
    /// list order, with one filter -- which defeats the point of having a scheduler. So the drive mode
    /// round-trips this type instead, and the node reconstructs the domain object verbatim.
    /// </para>
    /// <para>
    /// <see cref="CatalogIndex"/> travels as its raw numeric value. It is an opaque packed identifier
    /// (a <c>ulong</c>-backed enum of bit-fielded designations), so a number round-trips it exactly
    /// where a canonical string would need a parser on the way back in and could fail on a designation
    /// form the writer and reader disagree about.
    /// </para>
    /// </summary>
    public sealed class ScheduledObservationDto
    {
        public required string TargetName { get; init; }
        public required double TargetRA { get; init; }
        public required double TargetDec { get; init; }

        /// <summary>Raw <see cref="TianWen.Lib.Astrometry.Catalogs.CatalogIndex"/> value, or null for a
        /// synthesized target that is not a catalog object.</summary>
        public ulong? CatalogIndex { get; init; }

        /// <summary>Scheduler-chosen slot start. Honoured by the session loop's
        /// <c>WaitForScheduledStartAsync</c>, which is the whole reason this DTO exists.</summary>
        public required DateTimeOffset Start { get; init; }

        public required double DurationMinutes { get; init; }
        public required bool AcrossMeridian { get; init; }

        /// <summary>Per-filter exposure plan. Optional: an absent or empty plan is treated the same way
        /// as a single-filter target queued through <see cref="PendingTarget"/>.</summary>
        public ImmutableArray<FilterExposureDto> FilterPlan { get; init; } = [];

        public int? Gain { get; init; }
        public int? Offset { get; init; }

        /// <summary>
        /// <see cref="ObservationPriority"/>, defaulting to <c>Normal</c> when absent.
        /// <para>
        /// Travels as a <b>number</b>, not a name: this contract's source-generated context applies no
        /// string-enum conversion, so every enum on the wire is its ordinal. Our own client serializes
        /// through the same context so it round-trips either way, but a hand-written caller posting
        /// <c>"Normal"</c> would have its whole body rejected -- hence the default, so the field can
        /// simply be omitted.
        /// </para>
        /// </summary>
        public ObservationPriority Priority { get; init; } = ObservationPriority.Normal;

        public static ScheduledObservationDto FromScheduled(ScheduledObservation obs)
        {
            var plan = ImmutableArray.CreateBuilder<FilterExposureDto>(
                obs.FilterPlan.IsDefaultOrEmpty ? 0 : obs.FilterPlan.Length);
            if (!obs.FilterPlan.IsDefaultOrEmpty)
            {
                foreach (var fe in obs.FilterPlan)
                {
                    plan.Add(new FilterExposureDto
                    {
                        FilterPosition = fe.FilterPosition,
                        SubExposureSeconds = fe.SubExposure.TotalSeconds,
                        Count = fe.Count,
                    });
                }
            }

            return new ScheduledObservationDto
            {
                TargetName = obs.Target.Name,
                // A synthesized target (name known, coordinates not) carries NaN.
                TargetRA = JsonNumber.ForWire(obs.Target.RA),
                TargetDec = JsonNumber.ForWire(obs.Target.Dec),
                CatalogIndex = obs.Target.CatalogIndex is { } ci ? (ulong)ci : null,
                Start = obs.Start,
                DurationMinutes = obs.Duration.TotalMinutes,
                AcrossMeridian = obs.AcrossMeridian,
                FilterPlan = plan.MoveToImmutable(),
                Gain = obs.Gain,
                Offset = obs.Offset,
                Priority = obs.Priority,
            };
        }

        /// <summary>
        /// Rebuilds the domain object. Deliberately total (no validation failure mode): a filter plan
        /// that arrives empty stays empty, which the session already handles the same way it handles a
        /// single-filter target queued through <see cref="PendingTarget"/>.
        /// </summary>
        public ScheduledObservation ToScheduled()
        {
            var plan = ImmutableArray.CreateBuilder<FilterExposure>(
                FilterPlan.IsDefaultOrEmpty ? 0 : FilterPlan.Length);
            if (!FilterPlan.IsDefaultOrEmpty)
            {
                foreach (var fe in FilterPlan)
                {
                    plan.Add(new FilterExposure(
                        fe.FilterPosition,
                        TimeSpan.FromSeconds(fe.SubExposureSeconds),
                        fe.Count));
                }
            }

            return new ScheduledObservation(
                new Target(TargetRA, TargetDec, TargetName,
                    CatalogIndex is { } ci ? (CatalogIndex)ci : null),
                Start,
                TimeSpan.FromMinutes(DurationMinutes),
                AcrossMeridian,
                plan.MoveToImmutable(),
                Gain,
                Offset,
                Priority);
        }
    }

    /// <summary>One entry of a per-filter exposure plan.</summary>
    public sealed class FilterExposureDto
    {
        public required int FilterPosition { get; init; }
        public required double SubExposureSeconds { get; init; }
        public required int Count { get; init; }
    }
}
