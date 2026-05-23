namespace Trips.Core.Abstractions;

/// <summary>
/// Testable wall-clock abstraction. Production wires this to <c>SystemClock</c>; tests
/// inject a fake to control time deterministically.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
