using System.Text.RegularExpressions;

namespace Orchestrator.Domain.Workflows;

/// <summary>A node of the workflow DAG as seen by the validator.</summary>
public sealed record GraphNode(
    string Key,
    IReadOnlyList<string> DependsOn,
    string? ConfigJson = null,
    string? CompensationConfigJson = null);

/// <summary>A single validation problem. <see cref="Cycle"/> is populated for cycle errors.</summary>
public sealed record GraphError(string Code, string Message, string? StepKey = null, IReadOnlyList<string>? Cycle = null);

public static class GraphErrorCodes
{
    public const string EmptyWorkflow = "EmptyWorkflow";
    public const string TooManySteps = "TooManySteps";
    public const string InvalidKey = "InvalidKey";
    public const string DuplicateKey = "DuplicateKey";
    public const string SelfDependency = "SelfDependency";
    public const string UnknownDependency = "UnknownDependency";
    public const string Cycle = "Cycle";
    public const string InvalidTemplate = "InvalidTemplate";
    public const string UnknownTemplateReference = "UnknownTemplateReference";
    public const string TemplateReferenceNotUpstream = "TemplateReferenceNotUpstream";
}

public sealed class GraphValidationResult
{
    public GraphValidationResult(IReadOnlyList<GraphError> errors, IReadOnlyList<string> topologicalOrder)
    {
        Errors = errors;
        TopologicalOrder = topologicalOrder;
    }

    public IReadOnlyList<GraphError> Errors { get; }

    /// <summary>Deterministic topological order (ties broken by declaration order). Empty when invalid.</summary>
    public IReadOnlyList<string> TopologicalOrder { get; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// DAG validation and graph utilities. Cycle detection uses Kahn's algorithm: nodes left over after the
/// topological sort all sit on or behind a cycle, and walking dependencies inside that remainder is
/// guaranteed to revisit a node, which yields a concrete cycle to report.
/// </summary>
public static class WorkflowGraph
{
    public const int DefaultMaxSteps = 50;
    public const int MaxKeyLength = 100;

    private static readonly Regex KeyRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && KeyRegex.IsMatch(key);

    public static GraphValidationResult Validate(IReadOnlyList<GraphNode> nodes, int maxSteps = DefaultMaxSteps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var errors = new List<GraphError>();
        if (nodes.Count == 0)
        {
            errors.Add(new GraphError(GraphErrorCodes.EmptyWorkflow, "A workflow needs at least one step."));
            return new GraphValidationResult(errors, Array.Empty<string>());
        }

        if (nodes.Count > maxSteps)
        {
            errors.Add(new GraphError(
                GraphErrorCodes.TooManySteps,
                $"A workflow may contain at most {maxSteps} steps (got {nodes.Count})."));
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasDuplicates = false;
        for (var i = 0; i < nodes.Count; i++)
        {
            var key = nodes[i].Key ?? string.Empty;
            if (!IsValidKey(key))
            {
                errors.Add(new GraphError(
                    GraphErrorCodes.InvalidKey,
                    $"Step key '{key}' is invalid: use 1-{MaxKeyLength} letters, digits, '-' or '_', starting with a letter or digit.",
                    key));
            }

            if (!index.TryAdd(key, i))
            {
                hasDuplicates = true;
                if (!errors.Any(e => e.Code == GraphErrorCodes.DuplicateKey && e.StepKey == key))
                {
                    errors.Add(new GraphError(GraphErrorCodes.DuplicateKey, $"Step key '{key}' is used more than once.", key));
                }
            }
        }

        var dependencies = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            dependencies[i] = new List<int>();
            var key = nodes[i].Key ?? string.Empty;
            var declared = nodes[i].DependsOn ?? Array.Empty<string>();
            foreach (var dependency in declared.Distinct(StringComparer.Ordinal))
            {
                if (string.Equals(dependency, key, StringComparison.Ordinal))
                {
                    errors.Add(new GraphError(GraphErrorCodes.SelfDependency, $"Step '{key}' depends on itself.", key));
                    continue;
                }

                if (dependency is null || !index.TryGetValue(dependency, out var dependencyIndex))
                {
                    errors.Add(new GraphError(
                        GraphErrorCodes.UnknownDependency,
                        $"Step '{key}' depends on unknown step '{dependency}'.",
                        key));
                    continue;
                }

                dependencies[i].Add(dependencyIndex);
            }
        }

        if (hasDuplicates)
        {
            return new GraphValidationResult(errors, Array.Empty<string>());
        }

        var order = TopologicalSort(nodes, dependencies, out var cycle);
        if (cycle is not null)
        {
            errors.Add(new GraphError(
                GraphErrorCodes.Cycle,
                $"Cycle detected: {string.Join(" -> ", cycle)}.",
                cycle[0],
                cycle));
            return new GraphValidationResult(errors, Array.Empty<string>());
        }

        ValidateTemplates(nodes, dependencies, errors);
        return new GraphValidationResult(errors, errors.Count == 0 ? order : Array.Empty<string>());
    }

    /// <summary>All steps that (transitively) depend on any of <paramref name="roots"/>; roots excluded.</summary>
    public static IReadOnlySet<string> GetDescendants(IReadOnlyList<GraphNode> nodes, IEnumerable<string> roots)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var dependency in node.DependsOn)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                {
                    list = new List<string>();
                    dependents[dependency] = list;
                }

                list.Add(node.Key);
            }
        }

        var rootSet = roots.ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(rootSet);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!dependents.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (result.Add(child))
                {
                    stack.Push(child);
                }
            }
        }

        result.ExceptWith(rootSet);
        return result;
    }

    private static IReadOnlyList<string> TopologicalSort(
        IReadOnlyList<GraphNode> nodes,
        List<int>[] dependencies,
        out List<string>? cycle)
    {
        var count = nodes.Count;
        var inDegree = new int[count];
        var dependents = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            dependents[i] = new List<int>();
        }

        for (var i = 0; i < count; i++)
        {
            foreach (var dependency in dependencies[i])
            {
                inDegree[i]++;
                dependents[dependency].Add(i);
            }
        }

        // Priority = declaration index, so the order is stable and readable.
        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < count; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Enqueue(i, i);
            }
        }

        var order = new List<string>(count);
        while (ready.TryDequeue(out var current, out _))
        {
            order.Add(nodes[current].Key);
            foreach (var dependent in dependents[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    ready.Enqueue(dependent, dependent);
                }
            }
        }

        if (order.Count == count)
        {
            cycle = null;
            return order;
        }

        var remaining = new HashSet<int>(Enumerable.Range(0, count).Where(i => inDegree[i] > 0));
        cycle = FindCycle(nodes, dependencies, remaining);
        return order;
    }

    private static List<string> FindCycle(IReadOnlyList<GraphNode> nodes, List<int>[] dependencies, HashSet<int> remaining)
    {
        // Every node left over by Kahn's algorithm still has an unprocessed dependency inside the remainder,
        // so following dependencies must eventually revisit a node.
        var current = remaining.Min();
        var path = new List<int>();
        var position = new Dictionary<int, int>();
        while (!position.ContainsKey(current))
        {
            position[current] = path.Count;
            path.Add(current);
            var next = -1;
            foreach (var dependency in dependencies[current])
            {
                if (remaining.Contains(dependency) && (next == -1 || dependency < next))
                {
                    next = dependency;
                }
            }

            if (next == -1)
            {
                break;
            }

            current = next;
        }

        var start = position.TryGetValue(current, out var found) ? found : 0;
        var cycle = path.Skip(start).Select(i => nodes[i].Key).ToList();

        // The walk followed "depends on" edges; reverse it so the report reads in execution order.
        cycle.Reverse();
        cycle.Add(cycle[0]);
        return cycle;
    }

    private static void ValidateTemplates(IReadOnlyList<GraphNode> nodes, List<int>[] dependencies, List<GraphError> errors)
    {
        var keys = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var ancestors = CollectAncestors(i, dependencies).Select(a => nodes[a].Key).ToHashSet(StringComparer.Ordinal);
            CheckTemplates(nodes[i].Key, nodes[i].ConfigJson, ancestors, keys, allowSelf: false, errors);
            CheckTemplates(nodes[i].Key, nodes[i].CompensationConfigJson, ancestors, keys, allowSelf: true, errors);
        }
    }

    private static void CheckTemplates(
        string key,
        string? json,
        HashSet<string> ancestors,
        HashSet<string> keys,
        bool allowSelf,
        List<GraphError> errors)
    {
        foreach (var expression in TemplateSyntax.FindExpressions(json))
        {
            if (!TemplateSyntax.TryParse(expression, out var parsed, out var error))
            {
                errors.Add(new GraphError(
                    GraphErrorCodes.InvalidTemplate,
                    $"Step '{key}': invalid template expression '{expression}': {error}.",
                    key));
                continue;
            }

            if (parsed.Root != TemplateRoot.StepOutput || parsed.StepKey is null)
            {
                continue;
            }

            var target = parsed.StepKey;
            if (!keys.Contains(target))
            {
                errors.Add(new GraphError(
                    GraphErrorCodes.UnknownTemplateReference,
                    $"Step '{key}' references the output of unknown step '{target}'.",
                    key));
            }
            else if (!ancestors.Contains(target) && !(allowSelf && target == key))
            {
                errors.Add(new GraphError(
                    GraphErrorCodes.TemplateReferenceNotUpstream,
                    $"Step '{key}' references the output of '{target}', which is not one of its upstream dependencies; add it to dependsOn.",
                    key));
            }
        }
    }

    private static HashSet<int> CollectAncestors(int start, List<int>[] dependencies)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<int>(dependencies[start]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var dependency in dependencies[current])
            {
                stack.Push(dependency);
            }
        }

        return visited;
    }
}
