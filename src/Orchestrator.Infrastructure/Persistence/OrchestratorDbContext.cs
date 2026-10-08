using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orchestrator.Application.Abstractions;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Scheduling;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Infrastructure.Persistence;

public sealed class OrchestratorDbContext : DbContext, IOrchestratorDbContext
{
    // Every DateTime is stored as UTC; this restores DateTimeKind.Utc on materialisation (SQL Server and SQLite
    // both return Unspecified), so API payloads always carry the 'Z' suffix.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();

    public DbSet<StepDefinition> StepDefinitions => Set<StepDefinition>();

    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();

    public DbSet<StepRun> StepRuns => Set<StepRun>();

    public DbSet<ExecutionEvent> ExecutionEvents => Set<ExecutionEvent>();

    public DbSet<Schedule> Schedules => Set<Schedule>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CollectRunEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CollectRunEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(UtcConverter);
                }
            }
        }
    }

    /// <summary>
    /// Appends the execution events raised by tracked run aggregates to the same unit of work, so state changes
    /// and their audit trail commit atomically.
    /// </summary>
    private void CollectRunEvents()
    {
        var events = ChangeTracker.Entries<WorkflowRun>()
            .SelectMany(entry => entry.Entity.DequeueEvents())
            .ToList();
        if (events.Count > 0)
        {
            ExecutionEvents.AddRange(events);
        }
    }
}
