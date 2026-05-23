using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;

namespace Trips.Api.Auth;

/// <summary>
/// Issues JWT access tokens and opaque refresh tokens for authenticated users.
/// Tokens carry the user id (<c>sub</c>) and email so endpoints can derive the
/// caller without an extra database round-trip.
/// </summary>
public sealed class JwtTokenService
{
    private readonly AuthOptions _options;
    private readonly IClock _clock;

    public JwtTokenService(IOptions<AuthOptions> options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>
    /// Build an access + refresh token pair plus expiry. Caller assigns the refresh token to
    /// the user record (typically via the identity store) before returning to the client.
    /// </summary>
    public AuthTokenResponse Issue(IdentityUser user, string displayName)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (string.IsNullOrEmpty(_options.JwtKey))
        {
            throw new InvalidOperationException("Auth:JwtKey is not configured.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(_options.JwtKey);
        var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var expiresAt = _clock.UtcNow.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("name", displayName),
        };

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: _clock.UtcNow.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);
        var refreshToken = GenerateRefreshToken();

        return new AuthTokenResponse(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt,
            UserId: user.Id,
            Email: user.Email ?? string.Empty,
            DisplayName: displayName);
    }

    private static string GenerateRefreshToken()
    {
        Span<byte> buf = stackalloc byte[64];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf);
    }
}
