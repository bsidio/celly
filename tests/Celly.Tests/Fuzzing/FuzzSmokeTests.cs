using System.Text;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Fuzzing;

/// <summary>
/// Deterministic corpus-mutation fuzzing. The invariant under test is the safety contract for
/// hostile input: <c>Parse</c> reports issues but never throws, and <c>Check</c>/<c>Eval</c>
/// return values (including ErrorValue) but never let an exception escape. Seeds are fixed so
/// failures are reproducible; report failures with the seed + input.
/// </summary>
public class FuzzSmokeTests
{
    private static readonly string[] Corpus =
    [
        "1 + 2 * 3 == 7",
        "a && b || !c ? x.y[0] : f(1, 'two', 3.0)",
        "[1, 2, 3].filter(x, x % 2 == 0).map(x, x * x).exists(x, x > 4)",
        "{'k': v, 2: [true, null]}.k",
        "has(m.f) && m.all(k, m[k].startsWith('x'))",
        "timestamp('2024-01-01T00:00:00Z') + duration('1h') > now",
        "b'bytes' + b'\\xff' == b'more'",
        "'%s=%d'.format([name, 42]) .size() <= 100",
        "a.?b.orValue([?c, d]).size()",
        "type(dyn(1u)) == uint && int('42') / 7 >= -6",
        "Msg{f: 1, ?g: opt}.f",
        "\"\\u00e9\\U0001F600\" == s",
        "cel.bind(t, x * 2, t + t) in [[1], [2]]",
        "math.greatest(1, 2.5, 3u) < 4",
    ];

    private static readonly string Alphabet =
        "abcxyz_ABC019 \t\n.,:;?!'\"`(){}[]<>=+-*/%&|\\^~@#$" + "\u00e9 " + char.ConvertFromUtf32(0x1F600) + "\0";

    private static readonly Celly.CelEnv Env = Celly.CelEnv.Create(new Celly.CelEnvSettings
    {
        Libraries =
        [
            Celly.Extensions.OptionalsLibrary.Instance,
            Celly.Extensions.StringsLibrary.Instance,
            Celly.Extensions.MathLibrary.Instance,
            Celly.Extensions.BindingsLibrary.Instance,
        ],
    });

    private static void AssertSafe(string input, string origin)
    {
        try
        {
            var parsed = Env.Parse(input);
            if (parsed.Ast is null)
            {
                return; // rejected cleanly — the desired outcome for junk
            }

            _ = Env.Check(parsed.Ast); // must not throw regardless of outcome

            var program = Env.Program(parsed.Ast);
            var result = program.Eval(new Dictionary<string, object?>
            {
                ["a"] = true, ["b"] = false, ["c"] = true,
                ["x"] = 3L, ["v"] = "v", ["s"] = "s", ["name"] = "n",
                ["m"] = new Dictionary<string, object?> { ["f"] = "xv" },
                ["d"] = 1L, ["opt"] = 2L,
                ["now"] = TimestampValue.Of(1700000000, 0),
            });
            Assert.NotNull(result); // any CelValue (incl. ErrorValue) is acceptable
        }
        catch (Exception ex)
        {
            Assert.Fail($"{origin} crashed the pipeline: {ex.GetType().Name}: {ex.Message}\ninput: {input}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomJunkNeverCrashes(int seed)
    {
        var random = new Random(seed * 7919);
        for (var i = 0; i < 2500; i++)
        {
            var length = random.Next(0, 200);
            var sb = new StringBuilder(length);
            for (var j = 0; j < length; j++)
            {
                sb.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            AssertSafe(sb.ToString(), $"random(seed={seed}, i={i})");
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MutatedCorpusNeverCrashes(int seed)
    {
        var random = new Random(seed * 6271);
        for (var i = 0; i < 2500; i++)
        {
            var chars = new StringBuilder(Corpus[random.Next(Corpus.Length)]);
            var mutations = random.Next(1, 6);
            for (var m = 0; m < mutations && chars.Length > 0; m++)
            {
                switch (random.Next(4))
                {
                    case 0: // insert
                        chars.Insert(random.Next(chars.Length + 1), Alphabet[random.Next(Alphabet.Length)]);
                        break;
                    case 1: // delete
                        chars.Remove(random.Next(chars.Length), 1);
                        break;
                    case 2: // replace
                        chars[random.Next(chars.Length)] = Alphabet[random.Next(Alphabet.Length)];
                        break;
                    case 3: // duplicate a slice (breeds nesting and repetition)
                        var start = random.Next(chars.Length);
                        var len = Math.Min(random.Next(1, 30), chars.Length - start);
                        chars.Insert(start, chars.ToString(start, len));
                        break;
                }
            }

            AssertSafe(chars.ToString(), $"mutation(seed={seed}, i={i})");
        }
    }

    [Fact]
    public void PathologicalShapesNeverCrash()
    {
        string[] nasties =
        [
            new string('(', 100_000),
            new string('[', 50_000) + new string(']', 50_000),
            string.Concat(Enumerable.Repeat("[1].map(x, ", 2_000)) + "x" + new string(')', 2_000),
            string.Join("+", Enumerable.Repeat("1", 50_000)),
            "'" + new string('a', 1_000_000) + "'",
            new string('!', 100_000) + "true",
            "'%.'.format([1])",
            "'%.999999999f'.format([1.0])",       // precision bomb -> clean error
            "{" + string.Join(",", Enumerable.Range(0, 10_000).Select(i => $"{i}:{i}")) + "}",
            "\"\\",
            "b\"\\u0041\"",
            "0x",
            "1e",
            "..",
            "\ud800",                              // lone surrogate in source
        ];
        foreach (var nasty in nasties)
        {
            AssertSafe(nasty, "pathological");
        }
    }
}
