using System.Threading.Channels;

namespace Trips.Realtime.Eta;

/// <summary>
/// In-process queue of ETA-recompute jobs. A driver position update is enqueued by
/// <c>TripHub.PublishDriverPositionAsync</c> and consumed by <see cref="EtaRecomputeRunner"/>.
/// Decouples the hub method (must return fast) from the matrix recomputation (potentially costly).
/// </summary>
public sealed class EtaRecomputeQueue
{
    private readonly Channel<EtaRecomputeJob> _channel = Channel.CreateUnbounded<EtaRecomputeJob>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ChannelReader<EtaRecomputeJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(EtaRecomputeJob job, CancellationToken ct) =>
        _channel.Writer.WriteAsync(job, ct);
}

/// <summary>
/// One unit of work for the recompute runner: which trip + driver, and the driver's latest position.
/// </summary>
public sealed record EtaRecomputeJob(Guid TripId, Guid DriverId, double Latitude, double Longitude, DateTimeOffset Timestamp);
