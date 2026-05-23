using Xunit;

// Disable within-assembly parallelism so the SignalR + Postgres scaffolding doesn't compete with
// itself. Cross-assembly parallelism is handled separately by .runsettings (MaxCpuCount=1).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
