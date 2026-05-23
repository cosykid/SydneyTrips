namespace Trips.Optimisation.Common;

/// <summary>
/// Configurable knobs shared by every <see cref="Trips.Core.Abstractions.ISolver"/> implementation.
/// Lives in <c>Trips.Optimisation</c> rather than <c>Trips.Core.Abstractions</c> because the contract
/// already accepts a <see cref="CancellationToken"/>; this record simply gives the *default* wall-clock
/// cap (the spec calls it <c>SolverInput.TimeBudgetMs</c>) without expanding the public abstraction.
/// </summary>
/// <param name="TimeBudgetMs">Wall-clock cap per solver invocation. Defaults to 10 seconds.</param>
/// <param name="LogProgress">When true, solvers stream progress to the registered <see cref="Microsoft.Extensions.Logging.ILogger"/>.</param>
/// <param name="RandomSeed">Seed for any stochastic component (heuristic SA, OR-Tools randomisation).</param>
public sealed record SolverOptions(
    int TimeBudgetMs = 10_000,
    bool LogProgress = false,
    int RandomSeed = 42)
{
    public static SolverOptions Default { get; } = new();
}
