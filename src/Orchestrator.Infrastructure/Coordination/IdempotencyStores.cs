using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orchestrator.Application.Abstractions;
using StackExchange.Redis;

namespace Orchestrator.Infrastructure.Coordination;

/// <summary>In-process idempotency cache with TTL (tests / single-instance runs without Redis).</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (IdempotencyRecord Record, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryIdempotencyStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<IdempotencyRecord?> GetAsync(Guid workflowDefinitionId, string key, CancellationToken cancellationToken = default)
    {
        var cacheKey = IdempotencyKeys.For(workflowDefinitionId, key);
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            if (entry.ExpiresAt > _timeProvider.GetUtcNow())
            {
                return Task.FromResult<IdempotencyRecord?>(entry.Record);
            }

            _entries.TryRemove(cacheKey, out _);
        }

        return Task.FromResult<IdempotencyRecord?>(null);
    }

    public Task SetAsync(Guid workflowDefinitionId, string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _entries[IdempotencyKeys.For(workflowDefinitionId, key)] = (record, _timeProvider.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }
}

/// <summary>Redis-backed idempotency cache. Values are small JSON documents with a TTL.</summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _redis;

    public RedisIdempotencyStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<IdempotencyRecord?> GetAsync(Guid workflowDefinitionId, string key, CancellationToken cancellationToken = default)
    {
        var value = await _redis.GetDatabase().StringGetAsync(IdempotencyKeys.For(workflowDefinitionId, key));
        if (!value.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<IdempotencyRecord>(value.ToString());
    }

    public async Task SetAsync(Guid workflowDefinitionId, string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        await _redis.GetDatabase().StringSetAsync(
            IdempotencyKeys.For(workflowDefinitionId, key),
            JsonSerializer.Serialize(record),
            ttl,
            When.Always);
    }
}

internal static class IdempotencyKeys
{
    /// <summary>Client keys are hashed so arbitrary header values cannot shape the Redis key space.</summary>
    public static string For(Guid workflowDefinitionId, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"orchestrator:idempotency:{workflowDefinitionId:N}:{hash}";
    }
}

/// <summary>Readiness check: Redis answers PING.</summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await _redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:0.0} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", ex);
        }
    }
}
