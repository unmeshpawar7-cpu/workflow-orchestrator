using Microsoft.Extensions.Logging;
using Orchestrator.Application.Abstractions;
using StackExchange.Redis;

namespace Orchestrator.Infrastructure.Coordination;

/// <summary>
/// Single-process lock provider used for tests and local runs without Redis. Expiry honours the injected
/// <see cref="TimeProvider"/>, so it behaves like the Redis implementation under a fake clock.
/// </summary>
public sealed class InMemoryDistributedLockProvider : IDistributedLockProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _locks = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryDistributedLockProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<IDistributedLockHandle?> TryAcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_locks.TryGetValue(resource, out var existing) && existing.ExpiresAt > now)
            {
                return Task.FromResult<IDistributedLockHandle?>(null);
            }

            var token = Guid.NewGuid().ToString("N");
            _locks[resource] = (token, now + expiry);
            return Task.FromResult<IDistributedLockHandle?>(new Handle(this, resource, token));
        }
    }

    /// <summary>True when <paramref name="resource"/> is currently locked (diagnostics / tests).</summary>
    public bool IsHeld(string resource)
    {
        lock (_gate)
        {
            return _locks.TryGetValue(resource, out var existing) && existing.ExpiresAt > _timeProvider.GetUtcNow();
        }
    }

    private bool Release(string resource, string token)
    {
        lock (_gate)
        {
            // Compare-and-delete: never release a lock that expired and was re-acquired by someone else.
            if (_locks.TryGetValue(resource, out var existing) && existing.Token == token)
            {
                _locks.Remove(resource);
                return true;
            }

            return false;
        }
    }

    private sealed class Handle : IDistributedLockHandle
    {
        private readonly InMemoryDistributedLockProvider _owner;
        private int _released;

        public Handle(InMemoryDistributedLockProvider owner, string resource, string token)
        {
            _owner = owner;
            Resource = resource;
            Token = token;
        }

        public string Resource { get; }

        public string Token { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(Resource, Token);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Redis lock: <c>SET key token NX PX expiry</c> to acquire, Lua compare-and-delete to release. A Redis outage
/// degrades to "no lock" (logged) instead of stopping the engine, because the database claim protocol and
/// optimistic concurrency are what guarantee correctness.
/// </summary>
public sealed class RedisDistributedLockProvider : IDistributedLockProvider
{
    internal const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLockProvider> _logger;

    public RedisDistributedLockProvider(IConnectionMultiplexer redis, ILogger<RedisDistributedLockProvider> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IDistributedLockHandle?> TryAcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Guid.NewGuid().ToString("N");
        try
        {
            var database = _redis.GetDatabase();
            var acquired = await database.StringSetAsync(resource, token, expiry, When.NotExists);
            return acquired ? new RedisLockHandle(database, resource, token, _logger) : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis unavailable while acquiring {Resource}; continuing without the lock", resource);
            return new DegradedLockHandle(resource, token);
        }
    }

    private sealed class RedisLockHandle : IDistributedLockHandle
    {
        private readonly IDatabase _database;
        private readonly ILogger _logger;
        private int _released;

        public RedisLockHandle(IDatabase database, string resource, string token, ILogger logger)
        {
            _database = database;
            _logger = logger;
            Resource = resource;
            Token = token;
        }

        public string Resource { get; }

        public string Token { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                await _database.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { Resource }, new RedisValue[] { Token });
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // The key expires on its own; nothing else to do.
                _logger.LogWarning(ex, "Failed to release Redis lock {Resource}; it will expire", Resource);
            }
        }
    }

    private sealed class DegradedLockHandle : IDistributedLockHandle
    {
        public DegradedLockHandle(string resource, string token)
        {
            Resource = resource;
            Token = token;
        }

        public string Resource { get; }

        public string Token { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
