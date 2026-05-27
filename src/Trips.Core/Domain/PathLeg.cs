namespace Trips.Core.Domain;

/// <summary>
/// One mode-tagged segment of a participant's home → hub journey. <see cref="Mode"/> is the raw
/// TfNSW mode string ("walk", "train", "metro", "bus", "ferry", "lightrail", "unknown"); the map
/// colours each segment by it. <see cref="Points"/> is the segment geometry as [lng, lat] pairs.
/// Carried per-leg (not flattened) so the rendered journey shows its bus leg, train leg, etc. in
/// distinct colours the way Google Maps does.
/// <para>
/// The remaining fields back a Google-Maps-style timed itinerary in the planner hover:
/// <see cref="DurationMins"/> is the leg's travel time; <see cref="FromName"/>/<see cref="ToName"/>
/// are the stop names at each end; <see cref="RouteShortName"/> is the line label (e.g. "T1", "L2",
/// "389"); <see cref="DepartureTime"/>/<see cref="ArrivalTime"/> are the EFA-scheduled clock times.
/// All default so legacy / stub <see cref="PathLeg"/>s (and rows persisted before this feature)
/// deserialise cleanly from the <c>path_legs</c> jsonb column — the UI degrades to whatever's set.
/// </para>
/// </summary>
public sealed record PathLeg(
    string Mode,
    IReadOnlyList<PathPoint> Points,
    int DurationMins = 0,
    string? FromName = null,
    string? ToName = null,
    string? RouteShortName = null,
    DateTimeOffset? DepartureTime = null,
    DateTimeOffset? ArrivalTime = null);

/// <summary>A single [lng, lat] coordinate on a <see cref="PathLeg"/>. Longitude first, SRID 4326.</summary>
public sealed record PathPoint(double Lng, double Lat);
