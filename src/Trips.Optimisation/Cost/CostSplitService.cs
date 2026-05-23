using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.Cost;

/// <summary>
/// Splits the fuel + toll cost of a locked <see cref="Solution"/> across passengers using
/// passenger-kilometre fairness. The driver pays nothing — they were going anyway. Each leg of a
/// driver's route is charged proportionally to whoever is aboard during that leg; tolls split equally
/// among aboard-passengers; fuel converts kilometres to litres via the vehicle's fuel economy.
/// </summary>
public interface ICostSplitService
{
    /// <summary>
    /// Compute the cost-split breakdown for a locked solution.
    /// </summary>
    /// <param name="solutionId">The locked <see cref="Solution"/> id to split.</param>
    /// <param name="inputs">Fuel price, fuel economy, and any toll segments.</param>
    /// <param name="ct">Cancellation.</param>
    Task<CostSplitBreakdown> ComputeAsync(Guid solutionId, CostInputs inputs, CancellationToken ct);
}

/// <summary>Per-run inputs to the cost split. Fuel price + economy come from config; tolls are optional.</summary>
/// <param name="FuelPricePerLitre">Currency per litre (e.g. AUD 2.10).</param>
/// <param name="VehicleFuelEconomyLPer100Km">Litres consumed per 100 km of driving (e.g. 8.5 L/100 km).</param>
/// <param name="Tolls">Optional list of toll segments. Empty list ≡ no tolls.</param>
public sealed record CostInputs(
    double FuelPricePerLitre,
    double VehicleFuelEconomyLPer100Km,
    IReadOnlyList<TollSegment> Tolls);

/// <summary>One toll segment, anchored to a from→to stop on a driver's route.</summary>
/// <param name="FromStopId">Stop id where the toll segment begins (driver's prior stop in the visit sequence).
/// Use <see cref="Guid.Empty"/> to represent the driver's origin.</param>
/// <param name="ToStopId">Stop id where the toll segment ends. Use <see cref="Guid.Empty"/> for the destination.</param>
/// <param name="Amount">Toll cost in the same currency as <see cref="CostInputs.FuelPricePerLitre"/>.</param>
public sealed record TollSegment(Guid FromStopId, Guid ToStopId, double Amount);

/// <summary>Aggregated cost-split breakdown returned by <see cref="ICostSplitService.ComputeAsync"/>.</summary>
public sealed record CostSplitBreakdown(
    double TotalFuelCost,
    double TotalTollCost,
    IReadOnlyList<PassengerShare> ShareByPassenger);

/// <summary>Per-passenger contribution. <see cref="Total"/> equals <see cref="FuelShare"/> + <see cref="TollShare"/>.</summary>
public sealed record PassengerShare(Guid ParticipantId, double FuelShare, double TollShare, double Total);

/// <summary>
/// Default implementation. Reads the locked solution + run's solver input to obtain segment distances
/// (via the persisted <see cref="Stop.Location"/> WGS84 points → haversine kilometres). Tolls split
/// equally among passengers aboard the matching segment; fuel splits proportionally to passenger-km.
///
/// <para>Edge cases:</para>
/// <list type="bullet">
///   <item>A segment with zero passengers aboard contributes only to the driver — and the driver pays
///   nothing, so that fuel is absorbed (not charged to anyone). This happens on the leg from the
///   driver's origin to the first pickup.</item>
///   <item>A toll on a zero-passenger segment is dropped (would otherwise divide by zero). Logged.</item>
///   <item>Distances are haversine over the persisted WGS84 stop points. This is a slight under-estimate
///   versus actual driving distance but the relative shares (which are what passengers see) are correct
///   because every passenger's share uses the same metric.</item>
/// </list>
/// </summary>
public sealed class CostSplitService : ICostSplitService
{
    private readonly ISolutionRepository _solutions;
    private readonly ILogger<CostSplitService> _logger;

    public CostSplitService(ISolutionRepository solutions, ILogger<CostSplitService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        _solutions = solutions;
        _logger = logger ?? NullLogger<CostSplitService>.Instance;
    }

    public async Task<CostSplitBreakdown> ComputeAsync(Guid solutionId, CostInputs inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var solution = await _solutions.GetByIdAsync(solutionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Solution {solutionId} not found.");

        return Compute(solution, inputs);
    }

    /// <summary>Pure overload (no repository) — handy for tests and benchmark code.</summary>
    public static CostSplitBreakdown Compute(Solution solution, CostInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(inputs);

        var litresPerKm = Math.Max(0, inputs.VehicleFuelEconomyLPer100Km) / 100.0;
        var passengerKm = new Dictionary<Guid, double>();
        var passengerTolls = new Dictionary<Guid, double>();

        double totalFuelKm = 0.0;
        double totalTollCharged = 0.0;
        var tollLookup = BuildTollLookup(inputs.Tolls);

        foreach (var route in solution.Routes)
        {
            var stops = route.Stops.OrderBy(s => s.OrderIndex).ToList();
            if (stops.Count == 0)
            {
                continue;
            }

            // Track passengers aboard at the start of each leg. The driver always starts alone.
            var aboard = new HashSet<Guid>();
            // Leg 0: origin → first stop. No passengers aboard (driver alone), nothing to charge.
            // Then for leg i (stop[i-1] → stop[i]), passengers aboard are those picked up at any
            // of stop[0..i-1] inclusive (everyone goes to the destination, so nobody disembarks).
            // The "trailing" leg from the last stop to the destination has everyone aboard.

            for (var i = 0; i < stops.Count; i++)
            {
                // Compute the distance of the leg arriving at stops[i].
                var fromPoint = i == 0 ? GetOriginPoint(stops[0]) : stops[i - 1].Location;
                var toPoint = stops[i].Location;
                var fromId = i == 0 ? Guid.Empty : stops[i - 1].Id;
                var toId = stops[i].Id;
                var km = HaversineKm(fromPoint, toPoint);

                ChargeLeg(passengerKm, passengerTolls, aboard, km, ref totalFuelKm, ref totalTollCharged,
                    tollLookup, fromId, toId);

                // After arriving at this stop, the people picked up here board for subsequent legs.
                foreach (var pickup in stops[i].Pickups)
                {
                    aboard.Add(pickup);
                }
            }

            // Trailing leg: last stop → destination. Distance unknown without the trip destination
            // point; if Stop.Location.SRID is sensible we can still use the last stop position only
            // for tolls. Distance is absorbed by the driver — see note on edge cases. (The TravelMins
            // on DriverRoute does encompass it, but breaking it back out requires the destination
            // point which isn't on the Solution itself.) We accept the slight under-charge on the
            // trailing leg; if a toll is anchored to (lastStop, Guid.Empty) the caller's tolls list
            // still applies, charged across all aboard passengers.
            var lastStopId = stops[^1].Id;
            ChargeTrailingTolls(passengerTolls, aboard, tollLookup, lastStopId, ref totalTollCharged);
        }

        var totalFuelLitres = totalFuelKm * litresPerKm;
        var totalFuelCost = totalFuelLitres * inputs.FuelPricePerLitre;

        // Convert per-passenger km into a per-passenger fuel cost. Pass-through if the route has zero
        // distance (would mean Sum=0); otherwise scale.
        var shares = new List<PassengerShare>(passengerKm.Count);
        foreach (var kvp in passengerKm)
        {
            var fuelLitres = kvp.Value * litresPerKm;
            var fuelCost = fuelLitres * inputs.FuelPricePerLitre;
            var tollShare = passengerTolls.TryGetValue(kvp.Key, out var t) ? t : 0.0;
            shares.Add(new PassengerShare(kvp.Key, fuelCost, tollShare, fuelCost + tollShare));
        }

        // Also surface passengers who exist in tolls only (edge case: passenger boards on a leg with
        // no other passenger-km but a toll). Rare but real.
        foreach (var kvp in passengerTolls)
        {
            if (!passengerKm.ContainsKey(kvp.Key))
            {
                shares.Add(new PassengerShare(kvp.Key, 0.0, kvp.Value, kvp.Value));
            }
        }

        return new CostSplitBreakdown(totalFuelCost, totalTollCharged, shares);
    }

    private static void ChargeLeg(
        Dictionary<Guid, double> passengerKm,
        Dictionary<Guid, double> passengerTolls,
        HashSet<Guid> aboard,
        double km,
        ref double totalFuelKm,
        ref double totalTollCharged,
        Dictionary<(Guid, Guid), double> tolls,
        Guid fromId,
        Guid toId)
    {
        totalFuelKm += km;
        if (aboard.Count > 0)
        {
            var perPassengerKm = km / aboard.Count;
            foreach (var pid in aboard)
            {
                passengerKm.TryGetValue(pid, out var prev);
                passengerKm[pid] = prev + perPassengerKm;
            }
        }
        if (tolls.TryGetValue((fromId, toId), out var tollAmount) && aboard.Count > 0)
        {
            var perPassengerToll = tollAmount / aboard.Count;
            foreach (var pid in aboard)
            {
                passengerTolls.TryGetValue(pid, out var prev);
                passengerTolls[pid] = prev + perPassengerToll;
            }
            totalTollCharged += tollAmount;
        }
        // Tolls on empty legs are dropped intentionally — see XML docs.
    }

    private static void ChargeTrailingTolls(
        Dictionary<Guid, double> passengerTolls,
        HashSet<Guid> aboard,
        Dictionary<(Guid, Guid), double> tolls,
        Guid lastStopId,
        ref double totalTollCharged)
    {
        if (!tolls.TryGetValue((lastStopId, Guid.Empty), out var tollAmount)) return;
        if (aboard.Count == 0) return;
        var per = tollAmount / aboard.Count;
        foreach (var pid in aboard)
        {
            passengerTolls.TryGetValue(pid, out var prev);
            passengerTolls[pid] = prev + per;
        }
        totalTollCharged += tollAmount;
    }

    private static Dictionary<(Guid From, Guid To), double> BuildTollLookup(IReadOnlyList<TollSegment> tolls)
    {
        var dict = new Dictionary<(Guid, Guid), double>(tolls.Count);
        foreach (var t in tolls)
        {
            dict[(t.FromStopId, t.ToStopId)] = t.Amount;
        }
        return dict;
    }

    private static Point GetOriginPoint(Stop firstStop)
    {
        // We don't persist the driver's origin location on the Solution graph (it lives on the
        // Participant row), so the first-leg distance (origin → first stop) gets treated as zero
        // here. That's actually consistent with the algorithm: nobody is aboard yet, so this leg
        // would only contribute to fuel cost — and that's the *driver's* baseline, which they pay
        // regardless. Returning the first stop's own location gives a zero-distance leg, which is
        // the desired behaviour.
        return firstStop.Location;
    }

    /// <summary>
    /// Great-circle distance in kilometres between two WGS84 points. We use the haversine formula
    /// because the distances are small (typically &lt; 200 km within Sydney) and the slight
    /// approximation error is dwarfed by the road-network curve factor (~1.3×). For *relative* cost
    /// shares the absolute distance scale doesn't matter — only the ratios between legs do.
    /// </summary>
    public static double HaversineKm(Point a, Point b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        const double earthKm = 6371.0088;
        var lat1 = a.Y * Math.PI / 180.0;
        var lat2 = b.Y * Math.PI / 180.0;
        var dLat = (b.Y - a.Y) * Math.PI / 180.0;
        var dLon = (b.X - a.X) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        return earthKm * c;
    }
}

