using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// A feasible pickup point for a single <see cref="Participant"/>. The set of candidate nodes per
/// participant is what makes this DARP variant "flexible-pickup": the solver chooses one node
/// per assigned participant rather than being forced to visit the participant's home.
/// </summary>
public sealed class CandidateNode
{
    public Guid Id { get; private set; }
    public Guid ParticipantId { get; private set; }
    public NodeKind Kind { get; private set; }
    public Point Location { get; private set; }

    /// <summary>Walk minutes from participant's home to this node (0 when Kind=Home).</summary>
    public int WalkMins { get; private set; }

    /// <summary>Public-transport minutes from home to this node (0 when Kind=Home).</summary>
    public int PtMins { get; private set; }

    /// <summary>Stable external identifier (e.g. TfNSW stop_id) — useful for departure boards / GTFS-RT.</summary>
    public string? ExternalId { get; private set; }

    public string? DisplayName { get; private set; }

    /// <summary>
    /// Actual geometric path the participant follows from home to this node, sourced from the
    /// TfNSW trip-planner leg geometries (concatenated walk + PT legs). Null for the Home node
    /// (no path) and for any node generated against a stub / pre-polyline cache. When present,
    /// the FE map draws this instead of a crow-fly dashed straight line.
    /// </summary>
    public LineString? Path { get; private set; }

    private CandidateNode()
    {
        Location = default!;
    }

    public CandidateNode(
        Guid id,
        Guid participantId,
        NodeKind kind,
        Point location,
        int walkMins,
        int ptMins,
        string? externalId = null,
        string? displayName = null,
        LineString? path = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (walkMins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(walkMins));
        }

        if (ptMins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ptMins));
        }

        Id = id;
        ParticipantId = participantId;
        Kind = kind;
        Location = location;
        WalkMins = walkMins;
        PtMins = ptMins;
        ExternalId = externalId;
        DisplayName = displayName;
        Path = path;
    }

    /// <summary>Total minutes the participant pays to reach this node from home.</summary>
    public int TravelMins => WalkMins + PtMins;
}
