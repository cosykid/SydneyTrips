using System.Threading.Channels;
using Trips.Core.Domain;

namespace Trips.Api.Optimisation;

/// <summary>
/// In-process unbounded channel implementation of <see cref="IOptimisationJobQueue"/>.
/// Suitable for a single-instance deployment; for multi-instance we'd swap for Redis Streams,
/// but that's not in WS4's scope.
/// </summary>
public sealed class OptimisationJobQueue : IOptimisationJobQueue
{
    private readonly Channel<OptimisationJob> _channel = Channel.CreateUnbounded<OptimisationJob>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ChannelReader<OptimisationJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid tripId, Guid runId, ObjectiveWeights weights, SolverKind solver, bool repairHint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return _channel.Writer.WriteAsync(new OptimisationJob(tripId, runId, weights, solver, repairHint), ct);
    }
}
