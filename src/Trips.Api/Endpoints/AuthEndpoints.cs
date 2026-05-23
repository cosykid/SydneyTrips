using Microsoft.AspNetCore.Http.HttpResults;
using Trips.Api.Auth;
using Trips.Api.Validation;
using Trips.Core.Contracts;

namespace Trips.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync)
            .AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<RegisterRequest>>()
            .WithName("Register");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<LoginRequest>>()
            .WithName("Login");

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<RefreshRequest>>()
            .WithName("Refresh");

        return app;
    }

    private static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        AuthService service,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await service.RegisterAsync(request, ct).ConfigureAwait(false);
        return result.Succeeded
            ? TypedResults.Ok(result.Tokens!)
            : TypedResults.Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest, title: "Registration failed", extensions: TraceExtensions(http));
    }

    private static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AuthService service,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await service.LoginAsync(request, ct).ConfigureAwait(false);
        return result.Succeeded
            ? TypedResults.Ok(result.Tokens!)
            : TypedResults.Problem(detail: result.Error, statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", extensions: TraceExtensions(http));
    }

    private static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest request,
        AuthService service,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await service.RefreshAsync(request, ct).ConfigureAwait(false);
        return result.Succeeded
            ? TypedResults.Ok(result.Tokens!)
            : TypedResults.Problem(detail: result.Error, statusCode: StatusCodes.Status401Unauthorized, title: "Refresh failed", extensions: TraceExtensions(http));
    }

    private static IDictionary<string, object?> TraceExtensions(HttpContext http) =>
        new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier };
}
