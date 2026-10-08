using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Common;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Services;

namespace Orchestrator.Api.Controllers;

/// <summary>Workflow definitions (immutable, versioned DAGs) and the entry points to run or schedule them.</summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
[Produces("application/json")]
public sealed class WorkflowsController : ControllerBase
{
    private readonly IWorkflowDefinitionService _definitions;
    private readonly IRunService _runs;
    private readonly IScheduleService _schedules;

    public WorkflowsController(IWorkflowDefinitionService definitions, IRunService runs, IScheduleService schedules)
    {
        _definitions = definitions;
        _runs = runs;
        _schedules = schedules;
    }

    /// <summary>Registers a workflow definition (a new version if the name already exists).</summary>
    /// <remarks>
    /// The DAG is validated before anything is stored: duplicate keys, unknown dependencies, cycles (reported as
    /// a path, e.g. <c>a -> b -> a</c>), the step limit, registered handler types, and that every
    /// <c>{{steps.x.output}}</c> template references an upstream step. Requires <c>workflow.admin</c>.
    /// </remarks>
    /// <response code="201">Definition created.</response>
    /// <response code="400">Validation failed (field errors, including cycle details).</response>
    [HttpPost]
    [Authorize(Policy = Policies.Administer)]
    [ProducesResponseType(typeof(ApiResponse<WorkflowDefinitionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateWorkflow([FromBody] CreateWorkflowDefinitionRequest request, CancellationToken cancellationToken)
    {
        var definition = await _definitions.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetWorkflow), new { id = definition.Id }, ApiResponse.Ok(definition));
    }

    /// <summary>Lists workflow definitions (newest version first per name).</summary>
    /// <param name="name">Optional exact name filter (shows all versions of one workflow).</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size (max 100).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<WorkflowDefinitionSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListWorkflows(
        [FromQuery] string? name,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _definitions.ListAsync(name, PageRequest.Normalize(page, pageSize), cancellationToken);
        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>Gets a definition with its steps in topological order.</summary>
    /// <response code="404">Unknown definition.</response>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.Read)]
    [ProducesResponseType(typeof(ApiResponse<WorkflowDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkflow(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse.Ok(await _definitions.GetAsync(id, cancellationToken)));

    /// <summary>Starts a run (asynchronously). Supports <c>Idempotency-Key</c>.</summary>
    /// <remarks>
    /// Same key + same payload returns the existing run with 200; same key + different payload returns 409.
    /// The run is executed by the background engine; poll <c>GET /api/runs/{id}</c>. Rate limited per API key.
    /// </remarks>
    /// <param name="id">Workflow definition id.</param>
    /// <param name="request">Run input and optional delayed start.</param>
    /// <param name="idempotencyKey">Optional client-generated key (max 200 chars).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <response code="202">Run accepted; Location points at the run.</response>
    /// <response code="200">Idempotent replay: the run created earlier with this key.</response>
    /// <response code="409">Idempotency key reused with a different payload.</response>
    /// <response code="429">Rate limit exceeded.</response>
    [HttpPost("{id:guid}/runs")]
    [Authorize(Policy = Policies.Operate)]
    [EnableRateLimiting(RunCreationRateLimitOptions.PolicyName)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<RunDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateRun(
        Guid id,
        [FromBody] CreateRunRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await _runs.CreateAsync(id, request, idempotencyKey, HttpContext.TraceIdentifier, cancellationToken);
        if (!result.Created)
        {
            return Ok(ApiResponse.Ok(result.Run, "Idempotent replay: returning the run created earlier with this key."));
        }

        return AcceptedAtAction(nameof(RunsController.GetRun), "Runs", new { id = result.Run.Id }, ApiResponse.Ok(result.Run));
    }

    /// <summary>Creates a schedule (interval and/or one-off) that materialises runs of this definition.</summary>
    /// <response code="201">Schedule created.</response>
    [HttpPost("{id:guid}/schedules")]
    [Authorize(Policy = Policies.Operate)]
    [ProducesResponseType(typeof(ApiResponse<ScheduleDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateSchedule(Guid id, [FromBody] CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        var schedule = await _schedules.CreateAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(SchedulesController.GetSchedule), "Schedules", new { id = schedule.Id }, ApiResponse.Ok(schedule));
    }
}
