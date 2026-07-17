using Cel.Expr.Conformance.Test;
using Google.Protobuf;

namespace Celly.Conformance;

/// <summary>
/// Loads the vendored cel-spec conformance suite (testdata/*.binpb, encoded
/// <c>cel.expr.conformance.test.SimpleTestFile</c> messages — see tools/vendor-conformance.sh).
/// </summary>
public static class TestData
{
    public static readonly IReadOnlyDictionary<string, SimpleTest> Cases = Load();

    private static Dictionary<string, SimpleTest> Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata");
        var cases = new Dictionary<string, SimpleTest>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*.binpb").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            using var stream = File.OpenRead(path);
            var file = SimpleTestFile.Parser.ParseFrom(stream);
            foreach (var section in file.Section)
            {
                foreach (var test in section.Test)
                {
                    var id = $"{fileName}/{section.Name}/{test.Name}";
                    // A few sections reuse test names; disambiguate deterministically.
                    if (cases.ContainsKey(id))
                    {
                        var n = 2;
                        while (cases.ContainsKey($"{id}#{n}")) n++;
                        id = $"{id}#{n}";
                    }

                    cases.Add(id, test);
                }
            }
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException($"No conformance testdata found under '{dir}'. Run tools/vendor-conformance.sh.");
        }

        return cases;
    }
}
