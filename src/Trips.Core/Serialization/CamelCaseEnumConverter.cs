using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trips.Core.Serialization;

/// <summary>
/// JsonStringEnumConverter pre-bound to camelCase naming, so applying it via
/// <c>[JsonConverter(typeof(CamelCaseEnumConverter))]</c> on an enum yields
/// values like <c>"completed"</c>, <c>"orTools"</c>, etc. on both serialize
/// and deserialize. Integer input is still accepted (<c>allowIntegerValues</c>
/// defaults to true), so legacy callers posting numeric enum values continue
/// to work.
/// </summary>
public sealed class CamelCaseEnumConverter : JsonStringEnumConverter
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}
