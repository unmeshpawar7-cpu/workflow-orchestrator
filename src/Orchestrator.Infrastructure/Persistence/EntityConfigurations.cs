using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Scheduling;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Infrastructure.Persistence;

internal sealed class WorkflowDefinitionConfiguration : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        builder.ToTable("WorkflowDefinitions");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Name).HasMaxLength(WorkflowDefinition.MaxNameLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(WorkflowDefinition.MaxDescriptionLength);
        builder.HasIndex(d => new { d.Name, d.Version }).IsUnique();

        builder.HasMany(d => d.Steps)
            .WithOne()
            .HasForeignKey(s => s.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Steps)
            .HasField("_steps")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StepDefinitionConfiguration : IEntityTypeConfiguration<StepDefinition>
{
    public void Configure(EntityTypeBuilder<StepDefinition> builder)
    {
        builder.ToTable("StepDefinitions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Key).HasMaxLength(WorkflowGraph.MaxKeyLength).IsRequired();
        builder.Property(s => s.HandlerType).HasMaxLength(StepDefinition.MaxHandlerTypeLength).IsRequired();
        builder.Property(s => s.DependsOnJson).IsRequired();
        builder.Property(s => s.CompensationHandlerType).HasMaxLength(StepDefinition.MaxHandlerTypeLength);
        builder.Ignore(s => s.DependsOn);
        builder.Ignore(s => s.HasCompensation);
        builder.Ignore(s => s.Timeout);
        builder.Ignore(s => s.Retry);
        builder.HasIndex(s => new { s.WorkflowDefinitionId, s.Key }).IsUnique();
    }
}

internal sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.ToTable("WorkflowRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.InputJson).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(WorkflowRun.MaxIdempotencyKeyLength);
        builder.Property(r => r.RequestHash).HasMaxLength(64);
        builder.Property(r => r.CorrelationId).HasMaxLength(WorkflowRun.MaxCorrelationIdLength).IsRequired();
        builder.Property(r => r.Error).HasMaxLength(WorkflowRun.MaxErrorLength);
        builder.Property(r => r.Version).IsConcurrencyToken();

        builder.HasOne<WorkflowDefinition>()
            .WithMany()
            .HasForeignKey(r => r.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Steps)
            .WithOne()
            .HasForeignKey(s => s.WorkflowRunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Steps)
            .HasField("_steps")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Idempotency source of truth: one run per (definition, key).
        builder.HasIndex(r => new { r.WorkflowDefinitionId, r.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");

        // Coordinator: due Pending runs; idle Running/Compensating runs.
        builder.HasIndex(r => new { r.Status, r.ScheduledFor });

        // MaxConcurrentRuns check and listing by definition.
        builder.HasIndex(r => new { r.WorkflowDefinitionId, r.Status });

        builder.HasIndex(r => r.CreatedAt);
    }
}

internal sealed class StepRunConfiguration : IEntityTypeConfiguration<StepRun>
{
    public void Configure(EntityTypeBuilder<StepRun> builder)
    {
        builder.ToTable("StepRuns");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.StepKey).HasMaxLength(WorkflowGraph.MaxKeyLength).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.LeaseOwner).HasMaxLength(StepRun.MaxLeaseOwnerLength);
        builder.Property(s => s.LastError).HasMaxLength(StepRun.MaxErrorLength);
        builder.Property(s => s.Version).IsConcurrencyToken();
        builder.Ignore(s => s.IsLeased);

        builder.HasIndex(s => new { s.WorkflowRunId, s.StepKey }).IsUnique();

        // Worker claim query: WHERE Status IN (...) AND NextAttemptAt <= now ORDER BY NextAttemptAt.
        builder.HasIndex(s => new { s.Status, s.NextAttemptAt });

        // Lease recovery sweep: WHERE LeaseOwner IS NOT NULL AND LeaseExpiresAt < now.
        builder.HasIndex(s => s.LeaseExpiresAt);
    }
}

internal sealed class ExecutionEventConfiguration : IEntityTypeConfiguration<ExecutionEvent>
{
    public void Configure(EntityTypeBuilder<ExecutionEvent> builder)
    {
        builder.ToTable("ExecutionEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.StepKey).HasMaxLength(WorkflowGraph.MaxKeyLength);
        builder.Property(e => e.EventType).HasConversion<string>().HasMaxLength(64);
        builder.Property(e => e.Message).HasMaxLength(ExecutionEvent.MaxMessageLength).IsRequired();

        builder.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(e => e.WorkflowRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.WorkflowRunId, e.Id });
    }
}

internal sealed class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
{
    public void Configure(EntityTypeBuilder<Schedule> builder)
    {
        builder.ToTable("Schedules");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.InputJson).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken();

        builder.HasOne<WorkflowDefinition>()
            .WithMany()
            .HasForeignKey(s => s.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Coordinator: WHERE Enabled = 1 AND NextRunAt <= now ORDER BY NextRunAt.
        builder.HasIndex(s => new { s.Enabled, s.NextRunAt });
    }
}
