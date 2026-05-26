namespace Trips.Core.Domain;

/// <summary>
/// One mode-tagged segment of a participant's home → hub journey. <see cref="Mode"/> is the raw
/// TfNSW mode string ("walk", "train", "metro", "bus", "ferry", "lightrail", "unknown"); the map
/// colours each segment by it. <see cref="Points"/> is the segment geometry as [lng, lat] pairs.
/// Carried per-leg (not flattened) so the rendered journey shows its bus leg, train leg, etc. in
/// distinct colours the way Google Maps does.
/// </summary>
public sealed record PathLeg(string Mode, IReadOnlyList<PathPoint> Points);

/// <summary>A single [lng, lat] coordinate on a <see cref="PathLeg"/>. Longitude first, SRID 4326.</summary>
public sealed record PathPoint(double Lng, double Lat);
