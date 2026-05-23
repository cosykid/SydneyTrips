using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Abstractions;
using Trips.Optimisation.Common;
using Trips.Optimisation.Cost;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Postprocess;
using Trips.Optimisation.ReturnTrip;
using Trips.Optimisation.WhatIf;

namespace Trips.Optimisation;

/// <summary>
/// DI wiring for the optimisation stack. Callers (the API host, the bench harness) get a working
/// solver layer with one line — <c>services.AddTripsOptimisation()</c>.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Register every public type in <c>Trips.Optimisation</c>:
    /// <list type="bullet">
    ///   <item><see cref="OrToolsSolver"/> as the default <see cref="ISolver"/>.</item>
    ///   <item><see cref="HeuristicSolver"/> additionally registered with the key <c>"heuristic"</c>
    ///     so callers can pick deliberately via <c>IServiceProvider.GetKeyedService</c>.</item>
    ///   <item><see cref="SolutionPostprocessor"/> as a singleton; uses an
    ///     <see cref="IGoogleRoutesClient"/> when one is registered, otherwise falls back to
    ///     matrix-only times.</item>
    ///   <item><see cref="SolverOptions"/> bound to the default record; override via options.</item>
    ///   <item><see cref="SimulatedAnnealingSchedule"/> bound to the tuned default.</item>
    ///   <item>WS7 advanced services: <see cref="ICostSplitService"/>, <see cref="IReturnTripPlanner"/>,
    ///     <see cref="IWhatIfService"/>. Each requires a small repository adapter
    ///     (<see cref="ISolutionRepository"/>, <see cref="ILockedContextRepository"/>) which the data
    ///     layer wires up — the optimisation DI just declares the service registrations.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddTripsOptimisation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(SolverOptions.Default);
        services.TryAddSingleton(SimulatedAnnealingSchedule.Default);

        // Default ISolver = OR-Tools.
        services.AddSingleton<OrToolsSolver>();
        services.AddSingleton<HeuristicSolver>();
        services.AddSingleton<ISolver>(sp => sp.GetRequiredService<OrToolsSolver>());

        // Keyed registration so callers can opt into the heuristic explicitly:
        //     var heur = serviceProvider.GetRequiredKeyedService<ISolver>("heuristic");
        services.AddKeyedSingleton<ISolver>("heuristic", (sp, _) => sp.GetRequiredService<HeuristicSolver>());
        services.AddKeyedSingleton<ISolver>("or-tools", (sp, _) => sp.GetRequiredService<OrToolsSolver>());

        services.AddSingleton<SolutionPostprocessor>();

        // WS7 advanced features.
        services.TryAddSingleton(ReturnTripPlannerOptions.Default);
        services.AddScoped<ICostSplitService, CostSplitService>();
        services.AddScoped<IReturnTripPlanner, ReturnTripPlanner>();
        services.AddScoped<IWhatIfService, WhatIfService>();
        return services;
    }
}

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Polyfill for <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{T}(IServiceCollection, T)"/>
    /// so we don't pull in <c>Microsoft.Extensions.DependencyInjection.Extensions</c>.
    /// </summary>
    public static void TryAddSingleton<T>(this IServiceCollection services, T instance) where T : class
    {
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(T)) return;
        }
        services.AddSingleton(instance);
    }
}
