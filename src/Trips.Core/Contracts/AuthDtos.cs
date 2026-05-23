namespace Trips.Core.Contracts;

/// <summary>Payload to POST /auth/register.</summary>
public sealed record RegisterRequest(string Email, string Password, string DisplayName);

/// <summary>Payload to POST /auth/login.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Payload to POST /auth/refresh.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Response returned by /auth/login, /auth/register, /auth/refresh — contains a JWT and a refresh token.</summary>
public sealed record AuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserId,
    string Email,
    string DisplayName);
