using System;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// One axis's worth of guide correction: which way, and for how long.
/// </summary>
/// <remarks>
/// Exists so the two-axis overload of <c>PulseGuideAsync</c> can take an RA correction and a Dec
/// correction as two named, individually-optional arguments instead of four positional ones. A
/// collection would have been the obvious generalisation and is the wrong one: the axes are not
/// interchangeable, and a list invites "pulse East and West at once", which is not a diagonal
/// correction but a contradiction.
/// </remarks>
internal readonly record struct GuidePulse(GuideDirection Direction, TimeSpan Duration);
