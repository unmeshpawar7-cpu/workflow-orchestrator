using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrator.Infrastructure.Handlers;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API (including the hosted scheduler and worker) in the Testing environment against a temporary
/// SQLite file. A file database (rather than one shared in-memory connection) is used because the background
/// loops and HTTP requests use separate DbContexts concurrently, and a single SqliteConnection is not thread-safe.
/// Redis is disabled (in-memory lock + idempotency store); poll intervals are tiny (appsettings.Testing.json).
/// </summary>
public sealed class OrchestratorApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";
    public const string OperatorKey = "test-operator-key";
    public const string ReaderKey = "test-reader-key";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orchestrator-it-{Guid.NewGuid():N}.db");

    public OrchestratorApiFactory()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(ConnectionString).Options;
        using var db = new OrchestratorDbContext(options);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>Stand-in for a downstream service called by <c>http</c> steps; tests flip its status code.</summary>
    public DownstreamStub Downstream { get; } = new();

    private string ConnectionString => $"Data Source={_databasePath}";

    public HttpClient CreateClient(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrchestratorDbContext>>();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseSqlite(ConnectionString));
            services.AddHttpClient<HttpStepHandler>().ConfigurePrimaryHttpMessageHandler(() => Downstream);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // best-effort cleanup of a temp file
            }
        }
    }
}

public sealed class DownstreamStub : HttpMessageHandler
{
    private int _statusCode = (int)HttpStatusCode.OK;
    private int _calls;

    public HttpStatusCode StatusCode
    {
        get => (HttpStatusCode)Volatile.Read(ref _statusCode);
        set => Volatile.Write(ref _statusCode, (int)value);
    }

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        });
    }
}

public static class ApiTestExtensions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static async Task<Guid> CreateWorkflowAsync(this HttpClient adminClient, object definition)
    {
        var response = await adminClient.PostJsonAsync("/api/workflows", definition);
        var body = await response.ReadJsonAsync();
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Creating the workflow failed with {(int)response.StatusCode}: {body}");
        }

        return body.GetProperty("data").GetProperty("id").GetGuid();
    }

    public static async Task<Guid> StartRunAsync(this HttpClient client, Guid workflowId, object? input = null)
    {
        var response = await client.PostJsonAsync($"/api/workflows/{workflowId}/runs", new { input = input ?? new { } });
        var body = await response.ReadJsonAsync();
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"Starting the run failed with {(int)response.StatusCode}: {body}");
        }

        return body.GetProperty("data").GetProperty("id").GetGuid();
    }

    /// <summary>Polls GET /api/runs/{id} until <paramref name="isDone"/> holds or the timeout elapses.</summary>
    public static async Task<JsonElement> WaitForRunAsync(this HttpClient client, Guid runId, Func<JsonElement, bool> isDone, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var data = (await (await client.GetAsync($"/api/runs/{runId}")).ReadJsonAsync()).GetProperty("data");
            if (isDone(data))
            {
                return data;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Run {runId} did not reach the expected state; last seen: {data}");
            }

            await Task.Delay(100);
        }
    }

    public static Task<JsonElement> WaitForStatusAsync(this HttpClient client, Guid runId, string status, TimeSpan? timeout = null) =>
        client.WaitForRunAsync(runId, data => data.GetProperty("status").GetString() == status, timeout);
}
