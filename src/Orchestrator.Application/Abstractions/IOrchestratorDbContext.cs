using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Scheduling;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Abstractions;

/// <summary>
/// Persistence port used by the application layer. EF Core's <see cref="DbSet{TEntity}"/> is exposed on purpose:
/// the database is the work queue, and the claim protocol relies on relational semantics (conditional
/// <c>UPDATE ... WHERE</c> via <c>ExecuteUpdateAsync</c>) that a repository abstraction would only obscure.
/// See docs/adr/0001-db-as-queue-with-leases.md.
/// </summary>
public interface IOrchestratorDbContext
{
    DbSet<WorkflowDefinition> WorkflowDefinitions { get; }

    DbSet<StepDefinition> StepDefinitions { get; }

    DbSet<WorkflowRun> WorkflowRuns { get; }

    DbSet<StepRun> StepRuns { get; }

    DbSet<ExecutionEvent> ExecutionEvents { get; }

    DbSet<Schedule> Schedules { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
