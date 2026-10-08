using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Engine;
using Orchestrator.Infrastructure.Coordination;
using Orchestrator.Infrastructure.Handlers;
using Orchestrator.Infrastructure.Hosting;
using Orchestrator.Infrastructure.Persistence;
using StackExchange.Redis;

namespace Orchestrator.Infrastructure;

/// <summary>Configuration section <c>Redis</c>.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>When false, in-memory lock and idempotency implementations are used (single instance only).</summary>
    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = "localhost:6379";
}

/// <summary>Configuration section <c>Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public bool ApplyMigrationsOnStartup { get; set; }
}

public static class DependencyInjection
{
    public const string ConnectionStringName = "Orchestrator";

    /// <summary>Persistence (SQL Server), coordination (Redis or in-memory), step handlers and engine options.</summary>
    public static IServiceCollection AddOrchestratorInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
        services.AddDbContext<OrchestratorDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IOrchestratorDbContext>(sp => sp.GetRequiredService<OrchestratorDbContext>());

        services.AddOptions<EngineOptions>()
            .Bind(configuration.GetSection(EngineOptions.SectionName))
            .Validate(o => o.MaxStepsPerWorkflow is >= 1 and <= 500, "Engine:MaxStepsPerWorkflow must be between 1 and 500.")
            .ValidateOnStart();
        services.AddOptions<SchedulerOptions>()
            .Bind(configuration.GetSection(SchedulerOptions.SectionName))
            .Validate(o => o.PollInterval > TimeSpan.Zero, "Scheduler:PollInterval must be positive.")
            .Validate(o => o.BatchSize is >= 1 and <= 1000, "Scheduler:BatchSize must be between 1 and 1000.")
            .ValidateOnStart();
        services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .Validate(o => o.MaxDegreeOfParallelism is >= 1 and <= 256, "Worker:MaxDegreeOfParallelism must be between 1 and 256.")
            .Validate(
                o => o.PollInterval > TimeSpan.Zero && o.LeaseDuration > TimeSpan.Zero && o.HeartbeatInterval > TimeSpan.Zero,
                "Worker intervals must be positive.")
            .Validate(o => o.HeartbeatInterval < o.LeaseDuration, "Worker:HeartbeatInterval must be shorter than Worker:LeaseDuration.")
            .Validate(o => o.ClaimBatchSize >= 1 && o.RecoveryBatchSize >= 1, "Worker batch sizes must be at least 1.")
            .ValidateOnStart();
        services.AddOptions<HttpHandlerOptions>()
            .Bind(configuration.GetSection(HttpHandlerOptions.SectionName))
            .Validate(o => o.MaxResponseChars > 0, "Handlers:Http:MaxResponseChars must be positive.")
            .ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));

        var redis = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();
        if (redis.Enabled)
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisConfiguration = ConfigurationOptions.Parse(redis.ConnectionString);
                redisConfiguration.AbortOnConnectFail = false; // start even if Redis is briefly unavailable
                return ConnectionMultiplexer.Connect(redisConfiguration);
            });
            services.AddSingleton<IDistributedLockProvider, RedisDistributedLockProvider>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IDistributedLockProvider, InMemoryDistributedLockProvider>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }

        services.AddStepHandlers();
        return services;
    }

    /// <summary>Registers the built-in handlers; each is resolved by its <see cref="IStepHandler.Type"/> name.</summary>
    public static IServiceCollection AddStepHandlers(this IServiceCollection services)
    {
        services.AddHttpClient<HttpStepHandler>();
        services.AddTransient<IStepHandler>(sp => sp.GetRequiredService<HttpStepHandler>());
        services.AddTransient<IStepHandler, DelayStepHandler>();
        services.AddTransient<IStepHandler, LogStepHandler>();
        services.AddTransient<IStepHandler, TransformStepHandler>();
        services.AddTransient<IStepHandler, FaultInjectionStepHandler>();
        return services;
    }

    /// <summary>
    /// Hosted engine loops. Call from any host (the API today, a dedicated worker host tomorrow); each loop can be
    /// switched off independently with <c>Scheduler:Enabled</c> / <c>Worker:Enabled</c>.
    /// </summary>
    public static IServiceCollection AddOrchestratorWorkers(this IServiceCollection services)
    {
        services.AddHostedService<WorkflowSchedulerService>();
        services.AddHostedService<StepWorkerService>();
        return services;
    }
}
