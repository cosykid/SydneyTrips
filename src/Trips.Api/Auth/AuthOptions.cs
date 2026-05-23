namespace Trips.Api.Auth;

/// <summary>
/// Bound to the <c>Auth</c> configuration block. <see cref="JwtKey"/> should come from
/// <c>dotnet user-secrets</c> in development and from environment / KeyVault in production.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Symmetric signing key (HS256). Must be 32+ bytes after UTF-8 encoding.</summary>
    public string JwtKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "SydneyTrips";
    public string Audience { get; set; } = "SydneyTripsClient";

    /// <summary>Access-token lifetime. Defaults to one hour.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Refresh-token lifetime. Defaults to 14 days.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
}
