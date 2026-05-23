using System.Reflection;

namespace Trips.Mocks;

internal static class FixturePaths
{
    /// <summary>
    /// Locates the <c>Fixtures</c> directory next to the running assembly. Works under
    /// <c>dotnet test</c> (where fixtures are copied to the test output dir) and under
    /// <c>dotnet run --project tests/Mocks</c>.
    /// </summary>
    public static string FindFixturesRoot()
    {
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        var local = Path.Combine(asmDir, "Fixtures");
        if (Directory.Exists(local))
        {
            return local;
        }

        // Walk up the tree looking for tests/Mocks/Fixtures — handy in unit-test scenarios
        // where the fixture copy-to-output config has not picked up.
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Mocks", "Fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Fixtures directory not found relative to {asmDir}. " +
            $"Verify the Trips.Mocks project's CopyToOutputDirectory items.");
    }

    public static string ReadAll(string fixturesRoot, string relativePath)
    {
        var full = Path.Combine(fixturesRoot, relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Fixture missing: {full}");
        }
        return File.ReadAllText(full);
    }
}
