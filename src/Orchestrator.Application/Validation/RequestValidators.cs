using System.Text.Json;
using FluentValidation;
using Orchestrator.Application.Contracts;
using Orchestrator.Domain.Scheduling;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Validation;

public sealed class CreateWorkflowDefinitionRequestValidator : AbstractValidator<CreateWorkflowDefinitionRequest>
{
    public CreateWorkflowDefinitionRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(WorkflowDefinition.MaxNameLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9 ._-]*$")
            .WithMessage("Name may only contain letters, digits, spaces, '.', '_' and '-', and must start with a letter or digit.");
        RuleFor(x => x.Description).MaximumLength(WorkflowDefinition.MaxDescriptionLength);
        RuleFor(x => x.MaxConcurrentRuns).InclusiveBetween(1, WorkflowDefinition.MaxConcurrentRunsLimit);
        RuleFor(x => x.Steps).NotNull().NotEmpty().WithMessage("At least one step is required.");
        RuleForEach(x => x.Steps).NotNull().SetValidator(new StepDefinitionRequestValidator());
    }
}

public sealed class StepDefinitionRequestValidator : AbstractValidator<StepDefinitionRequest>
{
    public StepDefinitionRequestValidator()
    {
        RuleFor(x => x.Key)
            .Must(WorkflowGraph.IsValidKey)
            .WithMessage($"Key must be 1-{WorkflowGraph.MaxKeyLength} letters, digits, '-' or '_' and start with a letter or digit.");
        RuleFor(x => x.Handler).NotEmpty().MaximumLength(StepDefinition.MaxHandlerTypeLength);
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(StepDefinition.MinTimeoutSeconds, StepDefinition.MaxTimeoutSeconds);
        RuleFor(x => x.Config).Must(BeObjectOrNull).WithMessage("config must be a JSON object.");
        RuleFor(x => x.DependsOn).NotNull();
        RuleForEach(x => x.DependsOn).NotEmpty();

        When(x => x.Retry is not null, () =>
        {
            RuleFor(x => x.Retry!.MaxAttempts).InclusiveBetween(1, RetrySettings.MaxAllowedAttempts);
            RuleFor(x => x.Retry!.InitialDelayMs).InclusiveBetween(0, 3_600_000);
            RuleFor(x => x.Retry!.MaxDelayMs).InclusiveBetween(0, 86_400_000);
            RuleFor(x => x.Retry!.BackoffMultiplier).InclusiveBetween(1.0, RetrySettings.MaxAllowedMultiplier);
            RuleFor(x => x.Retry!.Jitter).InclusiveBetween(0.0, 1.0);
            RuleFor(x => x.Retry!)
                .Must(r => r.MaxDelayMs >= r.InitialDelayMs)
                .WithMessage("retry.maxDelayMs must be greater than or equal to retry.initialDelayMs.");
        });

        When(x => x.Compensation is not null, () =>
        {
            RuleFor(x => x.Compensation!.Handler).NotEmpty().MaximumLength(StepDefinition.MaxHandlerTypeLength);
            RuleFor(x => x.Compensation!.Config).Must(BeObjectOrNull).WithMessage("compensation.config must be a JSON object.");
        });
    }

    internal static bool BeObjectOrNull(JsonElement? element) =>
        element is null || element.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined;
}

public sealed class CreateRunRequestValidator : AbstractValidator<CreateRunRequest>
{
    public const int MaxInputBytes = 256 * 1024;

    public CreateRunRequestValidator()
    {
        RuleFor(x => x.Input)
            .Must(i => i is null || i.Value.GetRawText().Length <= MaxInputBytes)
            .WithMessage($"input must be at most {MaxInputBytes} bytes of JSON.");
    }
}

public sealed class CreateScheduleRequestValidator : AbstractValidator<CreateScheduleRequest>
{
    public CreateScheduleRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.IntervalSeconds is not null || x.RunAt is not null)
            .OverridePropertyName("intervalSeconds")
            .WithMessage("Provide intervalSeconds, runAt, or both.");
        RuleFor(x => x.IntervalSeconds)
            .InclusiveBetween(Schedule.MinIntervalSeconds, Schedule.MaxIntervalSeconds)
            .When(x => x.IntervalSeconds is not null);
        RuleFor(x => x.Input)
            .Must(i => i is null || i.Value.GetRawText().Length <= CreateRunRequestValidator.MaxInputBytes)
            .WithMessage($"input must be at most {CreateRunRequestValidator.MaxInputBytes} bytes of JSON.");
    }
}

public sealed class UpdateScheduleRequestValidator : AbstractValidator<UpdateScheduleRequest>
{
    public UpdateScheduleRequestValidator()
    {
        RuleFor(x => x.Enabled).NotNull().WithMessage("enabled is required.");
    }
}

public static class ValidatorExtensions
{
    /// <summary>Runs a FluentValidation validator and converts failures into a <see cref="Common.RequestValidationException"/>.</summary>
    public static async Task EnsureValidAsync<T>(this IValidator<T> validator, T instance, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (!result.IsValid)
        {
            throw new Common.RequestValidationException(
                "Validation failed",
                result.Errors.Select(e => new Common.ValidationFailure(ToCamelPath(e.PropertyName), e.ErrorMessage)).ToList());
        }
    }

    private static string ToCamelPath(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}
