using Trips.Mocks;

if (args.Length == 0 || !string.Equals(args[0], "start", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/Mocks -- start");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Starts WireMock servers on the fixed ports below so the frontend can hit them");
    Console.Error.WriteLine("without keys:");
    Console.Error.WriteLine("  - http://localhost:3001  TfNSW (trip / coord / departure_mon / gtfs feeds)");
    Console.Error.WriteLine("  - http://localhost:3002  Google Routes + Geocoding");
    Console.Error.WriteLine("  - http://localhost:3003  Nominatim");
    return 64; // EX_USAGE
}

var fixturesRoot = FixturePaths.FindFixturesRoot();
using var tfnsw = TfNswMockServer.Start(fixturesRoot, port: 3001);
using var google = GoogleRoutesMockServer.Start(fixturesRoot, port: 3002);
using var nominatim = NominatimMockServer.Start(fixturesRoot, port: 3003);

Console.WriteLine($"Mock servers started:");
Console.WriteLine($"  TfNSW       {tfnsw.BaseUrl}");
Console.WriteLine($"  Google      {google.BaseUrl}");
Console.WriteLine($"  Nominatim   {nominatim.BaseUrl}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");

var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.TrySetResult();
};

// Also exit cleanly when the host signals process shutdown (so `kill %1` from
// the verification scripts doesn't leave WireMock listeners holding the ports).
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.TrySetResult();

await shutdown.Task;
Console.WriteLine("Stopping mock servers");
return 0;
