using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Trips.Core.Contracts;

namespace Trips.Api.Auth;

/// <summary>
/// Thin wrapper over <see cref="UserManager{T}"/> exposing the three flows the API needs:
/// register, login (password), and refresh.
/// Display name is stored as a <see cref="ClaimTypes.Name"/> claim on the user.
/// Refresh tokens are persisted via Identity's user-token store so revocation is trivial.
/// </summary>
public sealed class AuthService
{
    private const string RefreshTokenProvider = "SydneyTrips";
    private const string RefreshTokenName = "RefreshToken";

    private readonly UserManager<IdentityUser> _userManager;
    private readonly JwtTokenService _tokenService;

    public AuthService(UserManager<IdentityUser> userManager, JwtTokenService tokenService)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(tokenService);
        _userManager = userManager;
        _tokenService = tokenService;
    }

    public async Task<AuthOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false);
        if (existing is not null)
        {
            return AuthOutcome.Failure("A user with that email already exists.");
        }

        var user = new IdentityUser
        {
            UserName = request.Email,
            Email = request.Email,
        };

        var createResult = await _userManager.CreateAsync(user, request.Password).ConfigureAwait(false);
        if (!createResult.Succeeded)
        {
            return AuthOutcome.Failure(string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        await _userManager.AddClaimAsync(user, new Claim(ClaimTypes.Name, request.DisplayName)).ConfigureAwait(false);

        var response = _tokenService.Issue(user, request.DisplayName);
        await PersistRefreshTokenAsync(user, response.RefreshToken).ConfigureAwait(false);
        return AuthOutcome.Success(response);
    }

    public async Task<AuthOutcome> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false);
        if (user is null)
        {
            return AuthOutcome.Failure("Invalid credentials.");
        }

        var ok = await _userManager.CheckPasswordAsync(user, request.Password).ConfigureAwait(false);
        if (!ok)
        {
            return AuthOutcome.Failure("Invalid credentials.");
        }

        var displayName = await GetDisplayNameAsync(user).ConfigureAwait(false);
        var response = _tokenService.Issue(user, displayName);
        await PersistRefreshTokenAsync(user, response.RefreshToken).ConfigureAwait(false);
        return AuthOutcome.Success(response);
    }

    public async Task<AuthOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Linear scan is fine for the dev scenarios this targets; for production a dedicated
        // refresh-token table indexed on the token value would be the next step. Materialise
        // first so we don't open concurrent commands on the same Npgsql connection.
        var users = await _userManager.Users.ToListAsync(ct).ConfigureAwait(false);
        foreach (var user in users)
        {
            var stored = await _userManager.GetAuthenticationTokenAsync(user, RefreshTokenProvider, RefreshTokenName)
                .ConfigureAwait(false);
            if (string.Equals(stored, request.RefreshToken, StringComparison.Ordinal))
            {
                var displayName = await GetDisplayNameAsync(user).ConfigureAwait(false);
                var response = _tokenService.Issue(user, displayName);
                await PersistRefreshTokenAsync(user, response.RefreshToken).ConfigureAwait(false);
                return AuthOutcome.Success(response);
            }
        }
        return AuthOutcome.Failure("Refresh token is invalid or has expired.");
    }

    private async Task PersistRefreshTokenAsync(IdentityUser user, string token)
    {
        await _userManager.RemoveAuthenticationTokenAsync(user, RefreshTokenProvider, RefreshTokenName).ConfigureAwait(false);
        await _userManager.SetAuthenticationTokenAsync(user, RefreshTokenProvider, RefreshTokenName, token).ConfigureAwait(false);
    }

    private async Task<string> GetDisplayNameAsync(IdentityUser user)
    {
        var claims = await _userManager.GetClaimsAsync(user).ConfigureAwait(false);
        return claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? user.UserName ?? string.Empty;
    }
}

/// <summary>Result wrapper for an auth flow — either a token bundle or a human-readable error.</summary>
public sealed record AuthOutcome(bool Succeeded, AuthTokenResponse? Tokens, string? Error)
{
    public static AuthOutcome Success(AuthTokenResponse tokens) => new(true, tokens, null);
    public static AuthOutcome Failure(string error) => new(false, null, error);
}
