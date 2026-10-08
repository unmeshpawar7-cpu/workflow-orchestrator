using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orchestrator.Application.Common;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Engine;

/// <summary>Values a template may reference: the run input and outputs of steps that have produced one.</summary>
public sealed class TemplateContext
{
    public TemplateContext(string? inputJson, IReadOnlyDictionary<string, string?> stepOutputs)
    {
        Input = Parse(inputJson);
        StepOutputs = stepOutputs
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => Parse(kv.Value), StringComparer.Ordinal);
    }

    public JsonNode? Input { get; }

    public IReadOnlyDictionary<string, JsonNode?> StepOutputs { get; }

    private static JsonNode? Parse(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

/// <summary>Template references that cannot be resolved are permanent, non-retryable failures.</summary>
public sealed class TemplateResolutionException : StepFailedException
{
    public TemplateResolutionException(string message)
        : base(message, retryable: false)
    {
    }
}

/// <summary>
/// Resolves <c>{{input.x}}</c> and <c>{{steps.key.output.x}}</c> placeholders inside handler configuration.
/// <list type="bullet">
/// <item>A string that is exactly one placeholder is replaced by the referenced JSON value (type preserved).</item>
/// <item>Placeholders embedded in longer strings are interpolated as text.</item>
/// <item>Only string values are inspected; property names are never templated.</item>
/// </list>
/// It is a path lookup over immutable JSON, not an expression evaluator: nothing is executed.
/// </summary>
public sealed class TemplateResolver
{
    public string? Resolve(string? configJson, TemplateContext context)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            throw new TemplateResolutionException($"Handler configuration is not valid JSON: {ex.Message}");
        }

        return ResolveNode(root, context)?.ToJsonString() ?? "null";
    }

    private static JsonNode? ResolveNode(JsonNode? node, TemplateContext context)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var resolvedObject = new JsonObject();
                foreach (var (name, child) in obj)
                {
                    resolvedObject[name] = ResolveNode(child, context);
                }

                return resolvedObject;
            case JsonArray array:
                var resolvedArray = new JsonArray();
                foreach (var item in array)
                {
                    resolvedArray.Add(ResolveNode(item, context));
                }

                return resolvedArray;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return ResolveString(text, context);
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? ResolveString(string text, TemplateContext context)
    {
        var matches = TemplateSyntax.Placeholder.Matches(text);
        if (matches.Count == 0)
        {
            return JsonValue.Create(text);
        }

        if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length)
        {
            return Lookup(matches[0].Groups[1].Value, context)?.DeepClone();
        }

        var interpolated = TemplateSyntax.Placeholder.Replace(
            text,
            (Match m) => ToText(Lookup(m.Groups[1].Value, context)));
        return JsonValue.Create(interpolated);
    }

    private static JsonNode? Lookup(string expression, TemplateContext context)
    {
        if (!TemplateSyntax.TryParse(expression, out var parsed, out var error))
        {
            throw new TemplateResolutionException($"Invalid template '{{{{{expression}}}}}': {error}.");
        }

        JsonNode? root;
        if (parsed.Root == TemplateRoot.Input)
        {
            root = context.Input;
        }
        else if (parsed.StepKey is not null && context.StepOutputs.TryGetValue(parsed.StepKey, out var output))
        {
            root = output;
        }
        else
        {
            throw new TemplateResolutionException($"Template '{{{{{expression}}}}}' references step '{parsed.StepKey}', which has no output.");
        }

        if (!JsonPath.TryGet(root, parsed.Path, out var value))
        {
            throw new TemplateResolutionException($"Template '{{{{{expression}}}}}' did not match any value.");
        }

        return value;
    }

    private static string ToText(JsonNode? node) =>
        node switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
}
