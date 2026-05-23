using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Postprocess;

namespace Trips.Optimisation.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddTripsOptimisation_RegistersDefaultSolverAsOrTools()
    {
        var services = new ServiceCollection();
        services.AddTripsOptimisation();
        using var sp = services.BuildServiceProvider();

        var solver = sp.GetRequiredService<ISolver>();
        Assert.Equal(SolverKind.OrTools, solver.Kind);
        Assert.IsType<OrToolsSolver>(solver);
    }

    [Fact]
    public void AddTripsOptimisation_RegistersHeuristicSolverByKey()
    {
        var services = new ServiceCollection();
        services.AddTripsOptimisation();
        using var sp = services.BuildServiceProvider();

        var solver = sp.GetRequiredKeyedService<ISolver>("heuristic");
        Assert.Equal(SolverKind.Heuristic, solver.Kind);
        Assert.IsType<HeuristicSolver>(solver);
    }

    [Fact]
    public void AddTripsOptimisation_RegistersOrToolsSolverByKey()
    {
        var services = new ServiceCollection();
        services.AddTripsOptimisation();
        using var sp = services.BuildServiceProvider();

        var solver = sp.GetRequiredKeyedService<ISolver>("or-tools");
        Assert.Equal(SolverKind.OrTools, solver.Kind);
    }

    [Fact]
    public void AddTripsOptimisation_RegistersPostprocessor()
    {
        var services = new ServiceCollection();
        services.AddTripsOptimisation();
        using var sp = services.BuildServiceProvider();

        var pp = sp.GetRequiredService<SolutionPostprocessor>();
        Assert.NotNull(pp);
    }
}
