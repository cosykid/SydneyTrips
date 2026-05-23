using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Trips.Integrations.Caching;

/// <summary>
/// Builds stable Redis cache keys from request payloads. Keys are short hex digests
/// of an XxHash64 over a deterministic JSON payload — collision risk is ~1 in 2^32
/// for the cache sizes we expect, and keys stay well under Redis' 512MB ceiling.
/// </summary>
internal static class CacheKey
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Stable property order matters for hashing — record types serialise their
        // primary-constructor properties in declaration order, which is what we want.
    };

    /// <summary>
    /// Build a key under the given namespace from one or more components.
    /// Components are serialised to JSON (so anonymous objects, records, and primitives
    /// all work) before being concatenated and hashed.
    /// </summary>
    public static string Build(string @namespace, params object[] components)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        ArgumentNullException.ThrowIfNull(components);

        var sb = new StringBuilder();
        foreach (var component in components)
        {
            sb.Append(component switch
            {
                null => "<null>",
                Point p => $"{p.Y.ToString("F6", CultureInfo.InvariantCulture)},{p.X.ToString("F6", CultureInfo.InvariantCulture)}",
                string s => s,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => JsonSerializer.Serialize(component, component.GetType(), JsonOptions),
            });
            sb.Append('|');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = XxHash64.HashToUInt64(bytes);
        return $"{@namespace}:{hash:x16}";
    }
}
