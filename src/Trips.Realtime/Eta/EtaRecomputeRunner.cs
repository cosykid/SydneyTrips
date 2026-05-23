using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trips.Realtime.Eta;

/// <summary>
/// Background service that drains <see cref="EtaRecomputeQueue"/> and runs <see cref="EtaService"/>
/// per job. Patterned after <c>OptimisationRunner</c> (the WS4 background runner): keep the hub
/// methods returning fast and let recomputation happen on a worker tied to the host lifetime.
/// </summary>
public sealed class EtaRecomputeRunner : BackgroundService
{
    private readonly EtaRecomputeQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<EtaRecomputeRunner> _logger;

    public EtaRecomputeRunner(
        EtaRecomputeQueue queue,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<EtaRecomputeRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.ApplicationStopping);
        _logger.LogInformation("EtaRecomputeRunner started");

        await foreach (var job in _queue.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<EtaService>();
                await service.RecomputeAndBroadcastAsync(job.TripId, job.DriverId, job.Latitude, job.Longitude, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ETA recompute failed for trip {Trip} driver {Driver}", job.TripId, job.DriverId);
            }
        }
    }
}
