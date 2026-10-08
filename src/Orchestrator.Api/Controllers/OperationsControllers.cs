using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Common;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Services;
using Orchestrator.Domain.Runs;

namespace Orchestrator.Api.Controllers;

/// <summary>Workflow runs: status, step states, execution history and operator commands.</summary>
[ApiController]
[Route("api/runs")]
[Authorize]
[Produces("application/json")]
public sealed class RunsController : ControllerBase
{
    private readonly IRunService _runs;

    public RunsController(IRunService runs)
    {
        _runs = runs;
    }

    /// <summary>Lists runs, newest first.</summary>
    /// <param name="status">Optional status filter (e.g. Running, Failed, DeadLettered).</param>
    /// <param name="definitionId">Optional workflow definition filter.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size (max 100).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RunSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListRuns(
        [FromQuery] WorkflowRunStatus? status,
        [FromQuery] Guid? definitionId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _runs.ListAsync(new RunFilter(status, definitionId), PageRequest.Normalize(page, pageSize), cancellationToken);
        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>Gets a run with the state of every step (attempts, lease, output, last error, duration).</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _runs.GetAsync(id, cancellationToken)));

    /// <summary>Append-only execution history (audit log) of a run, oldest first.</summary>
    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ExecutionEventDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(Guid id, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _runs.GetHistoryAsync(id, PageRequest.Normalize(page, pageSize), cancellationToken)));

    /// <summary>Cancels a Pending/Running run. In-flight steps are cancelled cooperatively by their worker.</summary>
    /// <response code="202">Cancellation recorded.</response>
    /// <response code="409">The run is not cancellable in its current state.</response>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.Operate)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelRun(Guid id, CancellationToken cancellationToken) =>
        Accepted(ApiResponse.Ok(await _runs.CancelAsync(id, cancellationToken)));

    /// <summary>Re-runs a Failed or DeadLettered run from its failed steps (and everything downstream).</summary>
    /// <response code="202">Run is Running again.</response>
    /// <response code="409">The run is not retryable in its current state.</response>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = Policies.Operate)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryRun(Guid id, CancellationToken cancellationToken) =>
        Accepted(ApiResponse.Ok(await _runs.RetryAsync(id, cancellationToken)));
}

/// <summary>Runs whose compensation failed permanently and need operator attention.</summary>
[ApiController]
[Route("api/dead-letters")]
[Authorize]
[Produces("application/json")]
public sealed class DeadLettersController : ControllerBase
{
    private readonly IRunService _runs;

    public DeadLettersController(IRunService runs)
    {
        _runs = runs;
    }

    /// <summary>Lists dead-lettered runs, newest first.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RunSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDeadLetters(
        [FromQuery] Guid? definitionId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _runs.ListAsync(
            new RunFilter(WorkflowRunStatus.DeadLettered, definitionId),
            PageRequest.Normalize(page, pageSize),
            cancellationToken);
        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>Requeues the failed compensations of a dead-lettered run (backward recovery).</summary>
    /// <response code="202">Run is Compensating again.</response>
    /// <response code="409">The run is not dead-lettered.</response>
    [HttpPost("{runId:guid}/requeue")]
    [Authorize(Policy = Policies.Operate)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Requeue(Guid runId, CancellationToken cancellationToken) =>
        Accepted(ApiResponse.Ok(await _runs.RequeueDeadLetterAsync(runId, cancellationToken)));
}

/// <summary>Schedules that create runs on an interval or at a fixed time.</summary>
[ApiController]
[Route("api/schedules")]
[Authorize]
[Produces("application/json")]
public sealed class SchedulesController : ControllerBase
{
    private readonly IScheduleService _schedules;

    public SchedulesController(IScheduleService schedules)
    {
        _schedules = schedules;
    }

    /// <summary>Lists schedules.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ScheduleDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSchedules(
        [FromQuery] Guid? definitionId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _schedules.ListAsync(definitionId, PageRequest.Normalize(page, pageSize), cancellationToken)));

    /// <summary>Gets a schedule.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<ScheduleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchedule(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _schedules.GetAsync(id, cancellationToken)));

    /// <summary>Enables or disables a schedule.</summary>
    /// <response code="409">A fired one-off schedule cannot be re-enabled.</response>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = Policies.Operate)]
    [ProducesResponseType(typeof(ApiResponse<ScheduleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSchedule(Guid id, [FromBody] UpdateScheduleRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _schedules.UpdateAsync(id, request, cancellationToken)));
}

/// <summary>Operational statistics.</summary>
[ApiController]
[Route("api/stats")]
[Authorize(Policy = Policies.Read)]
[Produces("application/json")]
public sealed class StatsController : ControllerBase
{
    private readonly IStatsService _stats;

    public StatsController(IStatsService stats)
    {
        _stats = stats;
    }

    /// <summary>Run/step counts by status, average successful step duration and retry counts (aggregate SQL).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<StatsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _stats.GetAsync(cancellationToken)));
}
