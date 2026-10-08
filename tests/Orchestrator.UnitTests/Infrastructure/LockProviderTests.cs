using FluentAssertions;
using Orchestrator.Application.Abstractions;
using Orchestrator.Infrastructure.Coordination;
using Orchestrator.UnitTests.TestSupport;

namespace Orchestrator.UnitTests.Infrastructure;

public sealed class InMemoryDistributedLockProviderTests
{
    private readonly ManualTimeProvider _clock = ManualTimeProvider.Default();

    [Fact]
    public async Task Lock_is_exclusive_until_released()
    {
        var provider = new InMemoryDistributedLockProvider(_clock);

        var first = await provider.TryAcquireAsync("run:1", TimeSpan.FromSeconds(10));
        var second = await provider.TryAcquireAsync("run:1", TimeSpan.FromSeconds(10));
        var other = await provider.TryAcquireAsync("run:2", TimeSpan.FromSeconds(10));

        first.Should().NotBeNull();
        second.Should().BeNull();
        other.Should().NotBeNull();
        provider.IsHeld("run:1").Should().BeTrue();

        await first!.DisposeAsync();

        provider.IsHeld("run:1").Should().BeFalse();
        (await provider.TryAcquireAsync("run:1", TimeSpan.FromSeconds(10))).Should().NotBeNull();
    }

    [Fact]
    public async Task Lock_expires_and_a_stale_owner_cannot_release_the_new_owners_lock()
    {
        var provider = new InMemoryDistributedLockProvider(_clock);
        var stale = await provider.TryAcquireAsync("run:1", TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(6));
        var fresh = await provider.TryAcquireAsync("run:1", TimeSpan.FromSeconds(5));
        await stale!.DisposeAsync();

        fresh.Should().NotBeNull();
        fresh!.Token.Should().NotBe(stale.Token);
        provider.IsHeld("run:1").Should().BeTrue("compare-and-delete must not remove a lock owned by another token");
    }

    [Fact]
    public async Task AcquireAsync_gives_up_after_the_wait_timeout()
    {
        var provider = new InMemoryDistributedLockProvider(_clock);
        await using var holder = await provider.TryAcquireAsync("run:1", TimeSpan.FromMinutes(1));

        var waited = await provider.AcquireAsync("run:1", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));

        waited.Should().BeNull();
    }

    [Fact]
    public async Task In_memory_idempotency_store_honours_ttl()
    {
        var store = new InMemoryIdempotencyStore(_clock);
        var definitionId = Guid.NewGuid();
        await store.SetAsync(definitionId, "key-1", new IdempotencyRecord(Guid.NewGuid(), "hash"), TimeSpan.FromMinutes(1));

        (await store.GetAsync(definitionId, "key-1")).Should().NotBeNull();
        (await store.GetAsync(Guid.NewGuid(), "key-1")).Should().BeNull("keys are scoped per definition");

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await store.GetAsync(definitionId, "key-1")).Should().BeNull();
    }

    [Fact]
    public void Redis_release_script_is_a_compare_and_delete()
    {
        RedisDistributedLockProvider.ReleaseScript.Should().Contain("redis.call('get', KEYS[1]) == ARGV[1]")
            .And.Contain("redis.call('del', KEYS[1])");
    }
}
