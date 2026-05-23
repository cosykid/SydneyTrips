using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Realtime.Hubs;

namespace Trips.Realtime.Gtfs;

/// <summary>
/// Polls TfNSW GTFS-Realtime feeds for the public-transport modes actively used by trips in
/// progress and broadcasts <c>EtaUpdated</c> for any passenger whose candidate node references a
/// stop that just received an update. Sleeps between cycles and re-resolves the active mode set
/// each cycle so we don't subscribe to feeds for trips no one is using right now.
/// </summary>
public sealed class GtfsRealtimeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GtfsRealtimeWorker> _logger;
    private readonly GtfsRealtimeOptions _options;

    public GtfsRealtimeWorker(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<GtfsRealtimeWorker> logger,
        IOptions<GtfsRealtimeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("GtfsRealtimeWorker disabled by configuration; exiting");
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.ApplicationStopping);
        var ct = linked.Token;
        _logger.LogInformation("GtfsRealtimeWorker started — poll interval {Seconds}s", _options.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GTFS-RT poll cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Test hook: run a single poll cycle with the same logic as the loop body.</summary>
    internal Task PollOnceForTestAsync(CancellationToken ct) => PollOnceAsync(ct);

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Per-cycle scope so EF/DbContext + HttpClient instances are owned by this cycle and
        // disposed promptly. The hosted service itself is a singleton, so we must scope manually.
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var tfNsw = sp.GetRequiredService<ITfNswClient>();
        var participantsRepo = sp.GetRequiredService<IParticipantRepository>();
        var hub = sp.GetRequiredService<IHubContext<TripHub, ITripHubClient>>();
        var index = await BuildActiveStopIndexAsync(sp, ct).ConfigureAwait(false);

        if (index.ModeToStopIds.Count == 0)
        {
            _logger.LogDebug("GTFS-RT poll: no active trips with PT-based candidate nodes; sleeping");
            return;
        }

        foreach (var (mode, watchedStops) in index.ModeToStopIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await foreach (var update in tfNsw.GtfsRtTripUpdatesAsync(mode, ct).ConfigureAwait(false))
                {
                    foreach (var stu in update.StopTimeUpdates)
                    {
                        if (!watchedStops.TryGetValue(stu.StopId, out var participants))
                        {
                            continue;
                        }
                        var arrival = (stu.Arrival ?? stu.Departure)?.UtcDateTime;
                        if (arrival is null)
                        {
                            continue;
                        }
                        foreach (var (tripId, participantId) in participants)
                        {
                            await hub.Clients
                                .Group(TripHub.GroupName(tripId))
                                .EtaUpdated(participantId, arrival.Value)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "GTFS-RT mode {Mode} has no configured feed; skipping", mode);
            }
            catch (NotImplementedException)
            {
                _logger.LogDebug("GTFS-RT mode {Mode} not supported by current client; skipping", mode);
            }
        }
    }

    /// <summary>
    /// Build a lookup of <c>mode → stopId → set of (tripId, participantId)</c> for trips that are
    /// active (i.e. not yet completed and within their depart-window) and whose participants have at
    /// least one PT-based candidate node. The mode is derived from the candidate-node kind.
    /// </summary>
    private static async Task<ActiveStopIndex> BuildActiveStopIndexAsync(IServiceProvider sp, CancellationToken ct)
    {
        var participants = sp.GetRequiredService<IParticipantRepository>();
        var tripRepo = sp.GetRequiredService<ITripRepository>();
        var clock = sp.GetRequiredService<IClock>();

        // We need: every trip whose depart-at is roughly "now" (some window either side), with its
        // participants and their candidate nodes loaded. There's no dedicated "list active trips"
        // method on the repo and adding one is out of scope for WS5 — so we list per-owner is too
        // narrow. Instead we iterate the trip-event log doesn't help; the cleanest path is to scan
        // candidate-nodes via participants per known trip. For this worker the realistic scenario
        // is admin/coord using a small N of trips at a time; we accept O(trips * participants) per
        // cycle. To stay polite we filter to participants whose trip is in [now-1h, now+3h].

        var index = new ActiveStopIndex();
        var now = clock.UtcNow;
        var earliest = now.AddHours(-1);
        var latest = now.AddHours(3);

        // Without a "list all trips" repository method we fall back to a per-trip scan keyed off
        // currently-loaded participants. The participants repository exposes ListForTripAsync but
        // not ListAll; pragmatic choice for WS5 is to load every participant via the repository's
        // List-for-trip surface by walking known trip ids from candidate-node ParticipantId joins.
        // That's recursive. The cleanest workaround: scan the TripEvents log for recent driver
        // position events — but we don't have a "list trips with recent events" surface either.
        //
        // Practical solution: the worker is a no-op until at least one driver has published a
        // position. Active trip discovery then piggybacks off the TripEvent log, which has the
        // exact "this trip is live" signal we want.
        var events = sp.GetRequiredService<ITripEventRepository>();
        var recentSince = now.AddHours(-2);
        var liveTripIds = new HashSet<Guid>();

        // We need an efficient "trips with events since X". The repository only exposes
        // per-trip queries — so we use one trip-events query per known trip discovered via the
        // owner-trips listing of the current call's authenticated context, which doesn't exist
        // here. Fall back to: the worker pulls from a small in-memory set populated by the hub.
        // (Implemented via ActiveTripRegistry; see usage below.)
        var registry = sp.GetService<IActiveTripRegistry>();
        if (registry is not null)
        {
            foreach (var id in registry.GetActiveTrips())
            {
                liveTripIds.Add(id);
            }
        }

        foreach (var tripId in liveTripIds)
        {
            ct.ThrowIfCancellationRequested();
            var trip = await tripRepo.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false);
            if (trip is null) continue;
            if (trip.DepartAt < earliest || trip.DepartAt > latest) continue;

            foreach (var participant in trip.Participants)
            {
                // Load candidate nodes per participant — that's the per-participant accessor we
                // already have on the repository contract.
                var withNodes = await participants.GetWithCandidateNodesAsync(participant.Id, ct).ConfigureAwait(false);
                if (withNodes is null) continue;
                foreach (var node in withNodes.CandidateNodes)
                {
                    if (string.IsNullOrWhiteSpace(node.ExternalId)) continue;
                    var mode = ModeForKind(node.Kind);
                    if (mode is null) continue;
                    index.Add(mode, node.ExternalId!, tripId, participant.Id);
                }
            }
        }

        return index;
    }

    private static string? ModeForKind(NodeKind kind) => kind switch
    {
        NodeKind.TrainStation => "trains",
        NodeKind.BusStop => "buses",
        NodeKind.Wharf => "ferries",
        NodeKind.LightRailStop => "lightrail",
        _ => null,
    };

    /// <summary>Per-mode lookup of <c>stopId → list of (tripId, participantId)</c>.</summary>
    internal sealed class ActiveStopIndex
    {
        public Dictionary<string, Dictionary<string, List<(Guid TripId, Guid ParticipantId)>>> ModeToStopIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string mode, string stopId, Guid tripId, Guid participantId)
        {
            if (!ModeToStopIds.TryGetValue(mode, out var stopMap))
            {
                stopMap = new Dictionary<string, List<(Guid, Guid)>>(StringComparer.OrdinalIgnoreCase);
                ModeToStopIds[mode] = stopMap;
            }
            if (!stopMap.TryGetValue(stopId, out var entries))
            {
                entries = new List<(Guid, Guid)>();
                stopMap[stopId] = entries;
            }
            entries.Add((tripId, participantId));
        }
    }
}

/// <summary>Bound to <c>Realtime:Gtfs</c>. Defaults are tuned for dev — a real deployment should override.</summary>
public sealed class GtfsRealtimeOptions
{
    public const string SectionName = "Realtime:Gtfs";

    /// <summary>Master switch. Disabled by default so a missing TfNSW API key doesn't break dev.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to poll all active modes. 30s mirrors the TfNSW feed cadence.</summary>
    public int PollIntervalSeconds { get; set; } = 30;
}
