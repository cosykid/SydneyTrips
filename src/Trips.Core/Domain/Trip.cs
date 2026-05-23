using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// Aggregate root representing one planned group trip.
/// Many participants depart from their respective Sydney origins, converging on a single
/// <see cref="Destination"/> within an <see cref="ArrivalWindow"/>.
/// </summary>
public sealed class Trip
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string DestinationName { get; private set; }
    public Point DestinationLocation { get; private set; }
    public DateTimeOffset DepartAt { get; private set; }
    public DateTimeOffset ArrivalWindowEarliest { get; private set; }
    public DateTimeOffset ArrivalWindowLatest { get; private set; }
    public Guid OwnerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? LockedSolutionId { get; private set; }

    private readonly List<Participant> _participants = new();
    public IReadOnlyList<Participant> Participants => _participants;

    private readonly List<OptimisationRun> _runs = new();
    public IReadOnlyList<OptimisationRun> Runs => _runs;

    // EF Core constructor — leave private and unbroken so the nullable annotations stay honest.
    private Trip()
    {
        Name = string.Empty;
        DestinationName = string.Empty;
        DestinationLocation = default!;
    }

    public Trip(
        Guid id,
        string name,
        Destination destination,
        DateTimeOffset departAt,
        ArrivalWindow arrivalWindow,
        Guid ownerId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(arrivalWindow);

        Id = id;
        Name = name;
        DestinationName = destination.Name;
        DestinationLocation = destination.Location;
        DepartAt = departAt;
        ArrivalWindowEarliest = arrivalWindow.Earliest;
        ArrivalWindowLatest = arrivalWindow.Latest;
        OwnerId = ownerId;
        CreatedAt = createdAt;
    }

    public Destination Destination => new(DestinationName, DestinationLocation);

    public ArrivalWindow ArrivalWindow => new(ArrivalWindowEarliest, ArrivalWindowLatest);

    public void LockSolution(Guid solutionId) => LockedSolutionId = solutionId;

    public void UnlockSolution() => LockedSolutionId = null;

    public void UpdateDestination(Destination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DestinationName = destination.Name;
        DestinationLocation = destination.Location;
    }
}
