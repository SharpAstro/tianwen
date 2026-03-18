using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Defines a single filter slot in a filter plan: which filter position to use,
/// the sub-exposure duration for that filter, and how many frames to capture
/// before advancing to the next filter in the plan.
/// </summary>
/// <param name="FilterPosition">Index into <see cref="Devices.IFilterWheelDriver.Filters"/>; -1 means no filter wheel / passthrough.</param>
/// <param name="SubExposure">Exposure duration per frame for this filter.</param>
/// <param name="Count">Number of frames to capture at this filter before advancing to the next entry in the plan.</param>
public readonly record struct FilterExposure(
    int FilterPosition,
    TimeSpan SubExposure,
    int Count = 1
);
