using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;
using Trips.Core.Abstractions;
using Trips.Core.Infrastructure;
using Trips.Data;

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

app.MapGet("/healthz", async (TripsDbContext db, CancellationToken ct) =>
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "degraded", detail = "database unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("HealthCheck")
    .WithTags("Health");

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
