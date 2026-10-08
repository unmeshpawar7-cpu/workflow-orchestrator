using System.Diagnostics;

namespace Orchestrator.Application.Abstractions;

/// <summary>A held distributed lock. Disposing releases it (compare-and-delete on the owner token).</summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    string Resource { get; }

    string Token { get; }
}

/// <summary>
/// Distributed mutual exclusion with automatic expiry. Used as defence in depth around per-run bookkeeping;
/// correctness never depends on it (see docs/adr/0002-redis-lock-defence-in-depth.md).
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>Single non-blocking attempt. Returns <c>null</c> when the lock is held by someone else.</summary>
    Task<IDistributedLockHandle?> TryAcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default);
}

public static class DistributedLockExtensions
{
    /// <summary>Polls <see cref="IDistributedLockProvider.TryAcquireAsync"/> until acquired or <paramref name="waitTimeout"/> elapses.</summary>
    public static async Task<IDistributedLockHandle?> AcquireAsync(
        this IDistributedLockProvider provider,
        string resource,
        TimeSpan expiry,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var handle = await provider.TryAcquireAsync(resource, expiry, cancellationToken);
            if (handle is not null || stopwatch.Elapsed >= waitTimeout)
            {
                return handle;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 40)), cancellationToken);
        }
    }
}

public static class LockKeys
{
    public static string Run(Guid runId) => $"orchestrator:lock:run:{runId:N}";
}

/// <summary>Cached idempotency decision: which run a key produced and the hash of the request that produced it.</summary>
public sealed record IdempotencyRecord(Guid RunId, string RequestHash);

/// <summary>
/// Fast-path cache for <c>Idempotency-Key</c> lookups. The unique index on (DefinitionId, IdempotencyKey) in the
/// database is the source of truth; a cache miss or cache outage only costs a database query.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> GetAsync(Guid workflowDefinitionId, string key, CancellationToken cancellationToken = default);

    Task SetAsync(Guid workflowDefinitionId, string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default);
}
