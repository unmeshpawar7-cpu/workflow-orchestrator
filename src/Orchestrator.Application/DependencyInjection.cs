using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Services;
using Orchestrator.Application.Validation;

namespace Orchestrator.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers use cases and the engine core. Persistence, locks, idempotency store and step handlers are
    /// supplied by the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddOrchestratorApplication(this IServiceCollection services)
    {
        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<CreateWorkflowDefinitionRequestValidator>();

        services.AddSingleton<TemplateResolver>();
        services.AddSingleton<DefinitionCache>();
        services.TryAddSingleton<IRetryPolicyFactory>(sp => new RetryPolicyFactory(sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<IStepHandlerResolver, StepHandlerResolver>();
        services.AddScoped<IDefinitionProvider, DefinitionProvider>();
        services.AddScoped<RunMutator>();
        services.AddScoped<IStepExecutor, StepExecutor>();
        services.AddScoped<IWorkflowCoordinator, WorkflowCoordinator>();

        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<IRunService, RunService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IStatsService, StatsService>();
        return services;
    }
}
