using System.Text.Json.Serialization;
using Trips.Core.Serialization;

namespace Trips.Core.Domain;

/// <summary>
/// Kind of a candidate pickup node. Drives where the participant rendezvous occurs
/// and what walk/PT travel time applies between Home and the chosen node.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum NodeKind
{
    Home = 0,
    TrainStation = 1,
    BusStop = 2,
    Wharf = 3,
    LightRailStop = 4,
}

/// <summary>
/// Lifecycle / runtime events emitted during trip planning and execution.
/// Persisted as <see cref="TripEvent"/> rows so late-joining clients can catch up.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum EventKind
{
    TripCreated = 0,
    ParticipantAdded = 1,
    ParticipantRemoved = 2,
    OptimisationStarted = 10,
    OptimisationCompleted = 11,
    OptimisationFailed = 12,
    SolutionLocked = 20,
    DriverDeparted = 30,
    DriverPositionUpdated = 31,
    DriverLate = 32,
    PassengerAtStop = 40,
    RouteRecomputed = 50,
}

/// <summary>
/// Status of an <see cref="OptimisationRun"/>.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum OptimisationStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// Which solver produced a <see cref="Solution"/>. Used by the benchmark harness
/// to compare OR-Tools against the custom heuristics.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum SolverKind
{
    OrTools = 0,
    Heuristic = 1,
}
