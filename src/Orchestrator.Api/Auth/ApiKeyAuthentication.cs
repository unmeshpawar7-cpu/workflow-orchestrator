using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Orchestrator.Api.Common;

namespace Orchestrator.Api.Auth;

public static class Roles
{
    public const string Admin = "workflow.admin";
    public const string Operator = "workflow.operator";
    public const string Reader = "workflow.reader";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Admin, Operator, Reader };
}

/// <summary>Authorization policies; roles are hierarchical (admin ⊇ operator ⊇ reader).</summary>
public static class Policies
{
    public const string Read = "workflow.read";
    public const string Operate = "workflow.operate";
    public const string Administer = "workflow.administer";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(Read, p => p.RequireRole(Roles.Reader, Roles.Operator, Roles.Admin));
        options.AddPolicy(Operate, p => p.RequireRole(Roles.Operator, Roles.Admin));
        options.AddPolicy(Administer, p => p.RequireRole(Roles.Admin));
    }
}

/// <summary>A configured client. Only the SHA-256 digest of the key is stored.</summary>
public sealed class ApiKeyEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower- or upper-case hex SHA-256 of the raw key (64 characters).</summary>
    public string KeyHash { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = new();
}

/// <summary>Configuration section <c>ApiKeys</c>.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";

    public List<ApiKeyEntry> Keys { get; set; } = new();

    public static bool IsValid(ApiKeyOptions options) =>
        options.Keys.All(k =>
            !string.IsNullOrWhiteSpace(k.Name)
            && k.KeyHash.Length == 64
            && k.KeyHash.All(c => Uri.IsHexDigit(c))
            && k.Roles.Count > 0
            && k.Roles.All(role => Roles.All.Contains(role)));
}

/// <summary>Constant-time matching of a presented key against the configured digests.</summary>
public static class ApiKeyMatcher
{
    public static ApiKeyEntry? Match(string presentedKey, IEnumerable<ApiKeyEntry> keys)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        ApiKeyEntry? match = null;

        // Compare against every key without early exit so timing does not reveal which entry matched.
        foreach (var key in keys)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(key.KeyHash);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presentedHash, expected) && match is null)
            {
                match = key;
            }
        }

        return match;
    }
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Machine-to-machine authentication via the <c>X-Api-Key</c> header. The key is hashed with SHA-256 and
/// compared in constant time with configured digests; the matching entry's name and roles become claims.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IOptionsMonitor<ApiKeyOptions> _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ApiKeyOptions> keys)
        : base(options, logger, encoder)
    {
        _keys = keys;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var entry = ApiKeyMatcher.Match(presented, _keys.CurrentValue.Keys);
        if (entry is null)
        {
            Logger.LogWarning("Rejected request with an unknown API key");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, entry.Name),
            new(ClaimTypes.Name, entry.Name),
        };
        claims.AddRange(entry.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["WWW-Authenticate"] = $"{SchemeName} header=\"{HeaderName}\"";
        await Response.WriteAsJsonAsync(ApiErrorResponse.Create($"A valid {HeaderName} header is required.", Context));
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(ApiErrorResponse.Create("The API key does not grant the role required for this operation.", Context));
    }
}
