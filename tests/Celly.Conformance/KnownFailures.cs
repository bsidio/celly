namespace Celly.Conformance;

/// <summary>
/// The ratcheting skip-list (testdata/known-failures.txt). Entries are one of:
/// <list type="bullet">
/// <item><c>file/section/test</c> — exact case id</item>
/// <item><c>prefix/*</c> — every case id starting with <c>prefix/</c></item>
/// <item><c>*</c> — everything (bootstrap state before the evaluator exists)</item>
/// </list>
/// Semantics enforced by <see cref="ConformanceTests"/>: a listed case that FAILS is an expected
/// failure (test passes); a listed case that PASSES fails the build until the entry is removed.
/// This keeps CI green while guaranteeing the pass rate only moves up.
/// </summary>
public static class KnownFailures
{
    private static readonly (HashSet<string> Exact, List<string> Prefixes, bool All) Entries = Load();

    public static bool Contains(string caseId)
    {
        if (Entries.All || Entries.Exact.Contains(caseId))
        {
            return true;
        }

        foreach (var prefix in Entries.Prefixes)
        {
            if (caseId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (HashSet<string>, List<string>, bool) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "known-failures.txt");
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>();
        var all = false;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line == "*")
            {
                all = true;
            }
            else if (line.EndsWith("/*", StringComparison.Ordinal))
            {
                prefixes.Add(line[..^1]); // keep trailing '/'
            }
            else
            {
                exact.Add(line);
            }
        }

        return (exact, prefixes, all);
    }
}
