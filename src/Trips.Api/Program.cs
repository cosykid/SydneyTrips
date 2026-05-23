using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using Trips.Realtime;
using Trips.Realtime.Hubs;

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

// Anonymous-session cookie: every browser gets a long-lived GUID in `trips_session`. Replaces
// JWT bearer + ASP.NET Identity entirely. Trip ownership compares that GUID against trip.OwnerId;
// non-owners can still read/modify the trip (anonymous share-link model), but only the owner
// session can delete.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentSession>();
builder.Services.AddScoped<TripAuthorizationService>();
// Same instance backs both the endpoint-side lookup and the hub-side lookup.
builder.Services.AddScoped<ITripHubAuthorizer>(sp => sp.GetRequiredService<TripAuthorizationService>());
builder.Services.AddScoped<ParticipantCandidateNodeService>();

// Background optimisation runner: a singleton queue + hosted service consumer.
builder.Services.Configure<OptimisationOptions>(builder.Configuration.GetSection(OptimisationOptions.SectionName));
builder.Services.AddSingleton<OptimisationJobQueue>();
builder.Services.AddSingleton<IOptimisationJobQueue>(sp => sp.GetRequiredService<OptimisationJobQueue>());
builder.Services.AddHostedService<OptimisationRunner>();

// SignalR hub + ETA recompute pipeline + GTFS-Realtime worker. See Trips.Realtime/DependencyInjection.
builder.Services.AddTripsRealtime(builder.Configuration);

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

// CORS — Next.js dev origin. AllowCredentials so the cookie flows on cross-origin XHR.
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
// Anonymous-session middleware runs early so endpoints and the SignalR hub both see the cookie
// GUID via CurrentSession. Set-Cookie on the response is appended before the body is flushed.
app.UseMiddleware<AnonymousSessionMiddleware>();

app.MapGet("/healthz", async (TripsDbContext db, CancellationToken ct) =>
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "degraded", detail = "database unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("HealthCheck")
    .WithTags("Health");

app.MapTrips();
app.MapParticipants();
app.MapOptimisation();
app.MapAdvanced();
app.MapCalendar();
app.MapEvents();

app.MapHub<TripHub>("/hubs/trip");

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

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> in tests can spin up the host.</summary>
public partial class Program;
