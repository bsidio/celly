using System.Text.RegularExpressions;
using Celly.Values;

namespace Celly.Stdlib;

/// <summary>Pluggable regex engine for <c>matches()</c> (and the M6 regex extension).</summary>
public interface IRegexEngine
{
    /// <summary>Unanchored (partial) match test; returns BoolValue or ErrorValue for bad patterns.</summary>
    CelValue IsMatch(string pattern, string input);
}

/// <summary>
/// RE2-semantics engine on pure managed .NET: <see cref="RegexOptions.NonBacktracking"/> gives the
/// linear-time guarantee and — like RE2 — rejects backreferences and lookaround. A small
/// translation pass bridges syntax differences (RE2's <c>(?P&lt;name&gt;</c> capture form).
/// </summary>
public sealed class NonBacktrackingRegexEngine : IRegexEngine
{
    public static readonly NonBacktrackingRegexEngine Instance = new();

    private const int CacheCapacity = 256;
    private readonly object _lock = new();
    private readonly Dictionary<string, Regex> _cache = [];

    public CelValue IsMatch(string pattern, string input)
    {
        Regex regex;
        try
        {
            regex = GetOrCompile(pattern);
        }
        catch (ArgumentException ex)
        {
            return new ErrorValue($"invalid regex: {ex.Message}");
        }

        return BoolValue.Of(regex.IsMatch(input));
    }

    private Regex GetOrCompile(string pattern)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }
        }

        var translated = TranslatePattern(pattern);
        var regex = new Regex(translated, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        lock (_lock)
        {
            if (_cache.Count >= CacheCapacity)
            {
                _cache.Clear(); // simple bound; full LRU is not worth the complexity here
            }

            _cache[pattern] = regex;
        }

        return regex;
    }

    /// <summary>RE2 named group syntax (?P&lt;name&gt;…) → .NET (?&lt;name&gt;…).</summary>
    private static string TranslatePattern(string pattern) =>
        pattern.Replace("(?P<", "(?<", StringComparison.Ordinal);
}
