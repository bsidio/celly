using System.Text;

namespace Celly.Common;

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>A diagnostic (parse or check error) with source position.</summary>
public sealed class CelIssue
{
    public CelIssue(IssueSeverity severity, Location location, string message)
    {
        Severity = severity;
        Location = location;
        Message = message;
    }

    public IssueSeverity Severity { get; }

    public Location Location { get; }

    public string Message { get; }

    public override string ToString() => $"{(Severity == IssueSeverity.Error ? "ERROR" : "WARNING")}: {Location}: {Message}";

    /// <summary>Formats the issue cel-go style, with a source snippet and caret line.</summary>
    public string ToDisplayString(Source source)
    {
        var sb = new StringBuilder();
        sb.Append($"{(Severity == IssueSeverity.Error ? "ERROR" : "WARNING")}: {source.Description}:{Location}: {Message}");
        var snippet = source.Snippet(Location.Line);
        if (snippet is not null)
        {
            sb.Append("\n | ").Append(snippet.Replace('\t', ' '));
            sb.Append("\n | ").Append(' ', Math.Max(0, Location.Column)).Append('^');
        }

        return sb.ToString();
    }
}

/// <summary>Collects diagnostics during parse/check phases.</summary>
public sealed class ErrorReporter
{
    private const int MaxErrors = 100;
    private readonly List<CelIssue> _issues = [];

    public IReadOnlyList<CelIssue> Issues => _issues;

    public bool HasErrors => _issues.Any(i => i.Severity == IssueSeverity.Error);

    public void ReportError(Location location, string message)
    {
        if (_issues.Count < MaxErrors)
        {
            _issues.Add(new CelIssue(IssueSeverity.Error, location, message));
        }
    }
}
