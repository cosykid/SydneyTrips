using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// A person taking part in a <see cref="Trip"/>. Either a driver (with car/seats) or a passenger.
/// </summary>
public sealed class Participant
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid TripId { get; private set; }
    public string DisplayName { get; private set; }
    public Point Home { get; private set; }
    public bool HasCar { get; private set; }
    public int Seats { get; private set; }

    // Preferences flattened — keeps the EF mapping simple and avoids an owned-type table.
    public int WalkBudgetMins { get; private set; }
    public int DetourToleranceMins { get; private set; }
    public double FairnessWeight { get; private set; }

    private readonly List<CandidateNode> _candidateNodes = new();
    public IReadOnlyList<CandidateNode> CandidateNodes => _candidateNodes;

    private Participant()
    {
        DisplayName = string.Empty;
        Home = default!;
    }

    public Participant(
        Guid id,
        Guid userId,
        Guid tripId,
        string displayName,
        Point home,
        bool hasCar,
        int seats,
        Preferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(preferences);
        if (seats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seats), "Seats cannot be negative.");
        }

        if (hasCar && seats < 1)
        {
            throw new ArgumentException("A driver must have at least one seat.", nameof(seats));
        }

        Id = id;
        UserId = userId;
        TripId = tripId;
        DisplayName = displayName;
        Home = home;
        HasCar = hasCar;
        Seats = seats;
        WalkBudgetMins = preferences.WalkBudgetMins;
        DetourToleranceMins = preferences.DetourToleranceMins;
        FairnessWeight = preferences.FairnessWeight;
    }

    public Preferences Preferences => new(WalkBudgetMins, DetourToleranceMins, FairnessWeight);

    public void UpdatePreferences(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        WalkBudgetMins = preferences.WalkBudgetMins;
        DetourToleranceMins = preferences.DetourToleranceMins;
        FairnessWeight = preferences.FairnessWeight;
    }

    public void AddCandidateNode(CandidateNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _candidateNodes.Add(node);
    }
}
