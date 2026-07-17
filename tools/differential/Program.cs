using System.Diagnostics;
using System.Globalization;
using System.Text;
using Celly;
using Celly.Checking;
using Celly.Types;
using Celly.Values;

namespace Celly.Differential;

/// <summary>
/// Differential fuzzer: generate random typed CEL expressions, evaluate each in Celly AND cel-go,
/// and report any result that differs. Errors match errors (messages ignored); values must match
/// exactly, with doubles compared by IEEE bit pattern so the comparison is language-independent.
///
/// Usage: dotnet run -c Release [count] [seed]
/// Requires the `go` toolchain on PATH for the cel-go side.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var count = args.Length > 0 ? int.Parse(args[0]) : 20_000;
        var seed = args.Length > 1 ? int.Parse(args[1]) : 1;

        var env = BuildEnv();
        var bindings = FixedBindings();

        Console.WriteLine($"Generating {count} expressions (seed {seed})…");
        var generator = new Generator(seed);
        var expressions = new List<string>(count);
        var cellyResults = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var expr = generator.Next();
            expressions.Add(expr);
            cellyResults.Add(EvalCelly(env, expr, bindings));
        }

        var dir = Path.Combine(Path.GetTempPath(), "celly-diff");
        Directory.CreateDirectory(dir);
        var exprFile = Path.Combine(dir, "exprs.txt");
        File.WriteAllLines(exprFile, expressions);

        Console.WriteLine("Evaluating the same expressions in cel-go…");
        var celgoResults = RunCelGo(exprFile);
        if (celgoResults is null)
        {
            Console.Error.WriteLine("cel-go run failed; is `go` on PATH?");
            return 2;
        }

        if (celgoResults.Count != expressions.Count)
        {
            Console.Error.WriteLine($"result count mismatch: celly {cellyResults.Count}, cel-go {celgoResults.Count}");
            return 2;
        }

        var divergences = 0;
        var sb = new StringBuilder();
        for (var i = 0; i < expressions.Count; i++)
        {
            if (cellyResults[i] != celgoResults[i])
            {
                divergences++;
                if (divergences <= 50)
                {
                    sb.AppendLine($"DIVERGE: {expressions[i]}");
                    sb.AppendLine($"   celly : {cellyResults[i]}");
                    sb.AppendLine($"   cel-go: {celgoResults[i]}");
                }
            }
        }

        Console.WriteLine();
        Console.Write(sb.ToString());
        Console.WriteLine($"=== {divergences} divergence(s) out of {count} expressions ===");
        return divergences == 0 ? 0 : 1;
    }

    private static CelEnv BuildEnv() => CelEnv.Create(new CelEnvSettings
    {
        Declarations =
        [
            new VariableDecl("i", CelType.Int),
            new VariableDecl("j", CelType.Int),
            new VariableDecl("u", CelType.Uint),
            new VariableDecl("d", CelType.Double),
            new VariableDecl("b", CelType.Bool),
            new VariableDecl("s", CelType.String),
            new VariableDecl("li", CelType.List(CelType.Int)),
            new VariableDecl("ls", CelType.List(CelType.String)),
            new VariableDecl("m", CelType.Map(CelType.String, CelType.Int)),
        ],
        Libraries = [Celly.Extensions.StringsLibrary.Instance],
    });

    private static Dictionary<string, object?> FixedBindings() => new()
    {
        ["i"] = 7L,
        ["j"] = -3L,
        ["u"] = 42UL,
        ["d"] = 2.5,
        ["b"] = true,
        ["s"] = "hello",
        ["li"] = new List<long> { 1, 2, 3, 4 },
        ["ls"] = new List<string> { "a", "bb" },
        ["m"] = new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 },
    };

    private static string EvalCelly(CelEnv env, string expr, IReadOnlyDictionary<string, object?> bindings)
    {
        try
        {
            var parsed = env.Parse(expr);
            if (parsed.Ast is null)
            {
                return "ERROR";
            }

            // Skip type-check: the generator is typed, and we want to compare runtime semantics.
            var program = env.Program(parsed.Ast);
            return Normalize(program.Eval(bindings));
        }
        catch (Exception)
        {
            return "EXCEPTION"; // never expected — a bug on our side if it appears
        }
    }

    /// <summary>Canonical result string; MUST match the Go normalizer byte-for-byte.</summary>
    private static string Normalize(CelValue value) => value switch
    {
        NullValue => "null",
        BoolValue b => "b:" + (b.Value ? "1" : "0"),
        IntValue x => "i:" + x.Value.ToString(CultureInfo.InvariantCulture),
        UintValue x => "u:" + x.Value.ToString(CultureInfo.InvariantCulture),
        DoubleValue x => "d:" + BitConverter.DoubleToInt64Bits(x.Value).ToString("x16", CultureInfo.InvariantCulture),
        StringValue x => "s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(x.Value)),
        BytesValue x => "y:" + Convert.ToHexString(x.Span).ToLowerInvariant(),
        ListValue x => "l:[" + string.Join(",", x.Elements.Select(Normalize)) + "]",
        MapValue x => "m:{" + string.Join(",", x.Keys
            .Select(k => (Key: Normalize(k), Val: x.TryGet(k, out var v) ? Normalize(v) : "?"))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Val)) + "}",
        TypeValue x => "t:" + x.Value.Name,
        ErrorValue => "ERROR",
        _ => "OTHER:" + value.Type.Name,
    };

    private static List<string>? RunCelGo(string exprFile)
    {
        var celgoDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "celgo");
        celgoDir = Path.GetFullPath(celgoDir);
        var psi = new ProcessStartInfo("go", $"run . \"{exprFile}\"")
        {
            WorkingDirectory = celgoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            var err = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine(err);
                return null;
            }

            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }
}
