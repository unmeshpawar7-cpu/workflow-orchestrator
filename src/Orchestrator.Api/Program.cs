using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Common;
using Orchestrator.Application;
using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Coordination;
using Orchestrator.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Engine + persistence. The hosted scheduler/worker loops run in this process but are self-contained
// BackgroundServices: a dedicated worker host only needs the first three calls below.
builder.Services.AddOrchestratorApplication();
builder.Services.AddOrchestratorInfrastructure(builder.Configuration);
builder.Services.AddOrchestratorWorkers();

builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value is not null && entry.Value.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiError(
                entry.Key,
                string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The value is invalid." : error.ErrorMessage)))
            .ToList();
        return new BadRequestObjectResult(ApiErrorResponse.Create("Validation failed", context.HttpContext, errors));
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Machine-to-machine authentication: X-Api-Key, SHA-256 digests in configuration, role-based policies.
builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .Validate(ApiKeyOptions.IsValid, "ApiKeys:Keys entries need a name, a 64-char hex SHA-256 KeyHash and known roles.")
    .ValidateOnStart();
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(Policies.Configure);

// Rate limiting of run creation: fixed window per API key (falls back to client IP).
builder.Services.AddOptions<RunCreationRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RunCreationRateLimitOptions.SectionName))
    .Validate(o => o.PermitLimit > 0 && o.WindowSeconds > 0, "RateLimiting:RunCreation values must be positive.")
    .ValidateOnStart();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            ApiErrorResponse.Create("Too many run creation requests; retry later.", context.HttpContext),
            cancellationToken);
    };
    options.AddPolicy(RunCreationRateLimitOptions.PolicyName, httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<RunCreationRateLimitOptions>>().Value;
        var partitionKey = httpContext.User.Identity?.Name
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.PermitLimit,
            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    });
});

var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrchestratorDbContext>("database", tags: new[] { "ready" });
if (builder.Configuration.GetValue<bool>("Redis:Enabled"))
{
    healthChecks.AddCheck<RedisHealthCheck>("redis", tags: new[] { "ready" });
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Workflow Orchestrator API",
        Version = "v1",
        Description = "Asynchronous DAG workflow orchestration: definitions, runs, retries, saga compensation, "
                      + "schedules and dead letters. Authenticate with the X-Api-Key header.",
        License = new OpenApiLicense { Name = "MIT" },
    });

    foreach (var xmlFile in new[] { "Orchestrator.Api.xml", "Orchestrator.Application.xml" })
    {
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }
    }

    options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAuthenticationHandler.HeaderName,
        Description = "API key (roles: workflow.admin, workflow.operator, workflow.reader).",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyAuthenticationHandler.SchemeName },
        }] = Array.Empty<string>(),
    });
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
}).AllowAnonymous();

if (!app.Environment.IsEnvironment("Testing")
    && app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
}

app.Run();

/// <summary>Entry point; partial so WebApplicationFactory can reference it in integration tests.</summary>
public partial class Program
{
}
