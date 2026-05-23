using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;
using Trips.Api.Auth;
using Trips.Api.Endpoints;
using Trips.Api.Optimisation;
using Trips.Api.Services;
using Trips.Api.Stubs;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Infrastructure;
using Trips.Data;
using Trips.Integrations;
using Trips.Optimisation;

const string DevCorsPolicy = "DevFrontend";

var builder = WebApplication.CreateBuilder(args);

// Serilog: console for dev, compact JSON for prod. Honours `Serilog` block in appsettings.
builder.Host.UseSerilog((ctx, sp, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(sp)
        .Enrich.FromLogContext();

    if (ctx.HostingEnvironment.IsDevelopment())
    {
        lc.WriteTo.Console();
    }
    else
    {
        lc.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// Data + Core services.
builder.Services.AddTripsData(builder.Configuration);
builder.Services.AddSingleton<IClock, SystemClock>();

// Integrations + Optimisation — real implementations register first so the
// Stubs/ TryAdd fallbacks below only kick in if config is missing (e.g. unit tests).
builder.Services.AddTripsIntegrations(builder.Configuration);
builder.Services.AddTripsOptimisation();

builder.Services.TryAddSingleton<ITfNswClient, StubTfNswClient>();
builder.Services.TryAddSingleton<IGoogleRoutesClient, StubGoogleRoutesClient>();
builder.Services.TryAddSingleton<IGeocodingClient, StubGeocodingClient>();
if (!builder.Services.Any(d => d.ServiceType == typeof(ISolver)))
{
    builder.Services.AddSingleton<ISolver, StubSolver>();
}

// Identity over Postgres — uses the same TripsDbContext.
builder.Services
    .AddIdentityCore<IdentityUser>(options =>
    {
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<TripsDbContext>()
    .AddDefaultTokenProviders();

// JWT bearer authentication.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// Apply an in-memory fallback for the signing key when the configuration source doesn't supply one.
// This keeps `dotnet run` painless in dev; production should always set Auth:JwtKey via secrets.
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AuthOptions.SectionName}:JwtKey"]))
{
    var generated = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    builder.Configuration[$"{AuthOptions.SectionName}:JwtKey"] = generated;
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// PostConfigure resolves the latest AuthOptions snapshot so issuer and validator agree on the key
// (matters in tests where WebApplicationFactory injects a different key after the builder runs).
builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services.AddAuthorization();

builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TripAuthorizationService>();
builder.Services.AddScoped<ParticipantCandidateNodeService>();

// Background optimisation runner: a singleton queue + hosted service consumer.
builder.Services.Configure<OptimisationOptions>(builder.Configuration.GetSection(OptimisationOptions.SectionName));
builder.Services.AddSingleton<OptimisationJobQueue>();
builder.Services.AddSingleton<IOptimisationJobQueue>(sp => sp.GetRequiredService<OptimisationJobQueue>());
builder.Services.AddHostedService<OptimisationRunner>();

// Validation.
builder.Services.AddValidatorsFromAssemblyContaining<CreateTripRequestValidator>(ServiceLifetime.Singleton);

// Problem-details + global exception handler.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// OpenAPI: minimal-API endpoint + Swagger UI for dev.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// CORS — Next.js dev origin.
builder.Services.AddCors(options =>
{
    options.AddPolicy(DevCorsPolicy, p => p
        .WithOrigins("http://localhost:3000")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Trips API v1"));
}

app.UseCors(DevCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", async (TripsDbContext db, CancellationToken ct) =>
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "degraded", detail = "database unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("HealthCheck")
    .WithTags("Health")
    .AllowAnonymous();

app.MapAuth();
app.MapTrips();
app.MapParticipants();
app.MapOptimisation();
app.MapAdvanced();
app.MapCalendar();

app.Run();

/// <summary>
/// Falls back to ProblemDetails for any otherwise-unhandled exception. Logged by Serilog request middleware.
/// </summary>
internal sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UnhandledExceptionHandler> _logger;

    public UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception while processing {Path}", httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "Unexpected server error.",
            status = StatusCodes.Status500InternalServerError,
            traceId = httpContext.TraceIdentifier,
        }, cancellationToken: cancellationToken);

        return true;
    }
}

/// <summary>
/// Resolves <see cref="AuthOptions"/> from the latest <see cref="Microsoft.Extensions.Options.IOptions{T}"/>
/// snapshot when configuring the JWT bearer validation parameters. Decouples validator setup from
/// the order in which configuration providers are stacked — important under
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>.
/// </summary>
internal sealed class ConfigureJwtBearerOptions : Microsoft.Extensions.Options.IPostConfigureOptions<JwtBearerOptions>
{
    private readonly Microsoft.Extensions.Options.IOptions<AuthOptions> _authOptions;

    public ConfigureJwtBearerOptions(Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions)
    {
        _authOptions = authOptions;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        var auth = _authOptions.Value;
        if (string.IsNullOrWhiteSpace(auth.JwtKey))
        {
            throw new InvalidOperationException("Auth:JwtKey is not configured.");
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = auth.Issuer,
            ValidAudience = auth.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> in tests can spin up the host.</summary>
public partial class Program;
