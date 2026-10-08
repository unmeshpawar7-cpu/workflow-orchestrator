using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orchestrator.Domain.Workflows;

public enum TemplateRoot
{
    /// <summary><c>{{input.path}}</c> - the run input.</summary>
    Input = 0,

    /// <summary><c>{{steps.key.output.path}}</c> - the output of an upstream step.</summary>
    StepOutput = 1,
}

/// <summary>A parsed <c>{{ ... }}</c> placeholder.</summary>
public sealed record TemplateExpression(TemplateRoot Root, string? StepKey, IReadOnlyList<string> Path);

/// <summary>
/// Grammar of the (deliberately tiny) template language used in handler configuration:
/// <code>
/// placeholder := "{{" ws expression ws "}}"
/// expression  := "input" ("." segment)*  |  "steps." key ".output" ("." segment)*
/// segment     := [A-Za-z0-9_-]+          (numeric segments index into arrays)
/// </code>
/// There is no evaluation, no functions and no scripting: a placeholder is a path lookup and nothing else.
/// </summary>
public static class TemplateSyntax
{
    public static readonly Regex Placeholder = new(
        @"\{\{\s*([^{}]*?)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Segment = new(
        "^[A-Za-z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Returns the raw expressions of every placeholder found in <paramref name="text"/>.</summary>
    public static IReadOnlyList<string> FindExpressions(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        return Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToList();
    }

    public static bool TryParse(
        string expression,
        [NotNullWhen(true)] out TemplateExpression? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        var segments = expression.Split('.');
        if (segments.Any(s => !Segment.IsMatch(s)))
        {
            error = "segments may only contain letters, digits, '-' and '_'";
            return false;
        }

        if (segments[0] == "input")
        {
            parsed = new TemplateExpression(TemplateRoot.Input, null, segments.Skip(1).ToArray());
            error = null;
            return true;
        }

        if (segments[0] == "steps")
        {
            if (segments.Length < 3 || segments[2] != "output")
            {
                error = "step references must have the form steps.<key>.output[.path]";
                return false;
            }

            parsed = new TemplateExpression(TemplateRoot.StepOutput, segments[1], segments.Skip(3).ToArray());
            error = null;
            return true;
        }

        error = $"unknown root '{segments[0]}' (supported: input, steps)";
        return false;
    }
}
