using System.Globalization;
using System.Text;

namespace Celly.Differential;

/// <summary>
/// Generates random, type-correct CEL expressions over a fixed set of declared variables. Type
/// correctness keeps both engines actually evaluating (rather than mostly type-erroring), so the
/// comparison exercises real evaluation semantics. Deterministic given a seed.
///
/// Declared variables (identical on both sides): i,j:int  u:uint  d:double  b:bool  s:string
/// li:list(int)  ls:list(string)  m:map(string,int).
/// </summary>
public sealed class Generator(int seed)
{
    private readonly Random _random = new(seed);
    private int _budget;

    private enum Kind { Int, Uint, Double, Bool, String, ListInt, MapStrInt }

    public string Next()
    {
        _budget = 40; // node budget per expression, keeps trees bounded
        var kind = (Kind)_random.Next(Enum.GetValues<Kind>().Length);
        return Gen(kind, depth: 0, iterVar: null);
    }

    private string Gen(Kind kind, int depth, string? iterVar)
    {
        var atom = depth >= 4 || _budget-- <= 0 || _random.Next(100) < 25;
        return kind switch
        {
            Kind.Int => GenInt(depth, iterVar, atom),
            Kind.Uint => GenUint(depth, iterVar, atom),
            Kind.Double => GenDouble(depth, iterVar, atom),
            Kind.Bool => GenBool(depth, iterVar, atom),
            Kind.String => GenString(depth, iterVar, atom),
            Kind.ListInt => GenListInt(depth, iterVar, atom),
            Kind.MapStrInt => "m",
            _ => "0",
        };
    }

    private string GenInt(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(6) switch
            {
                0 => "i",
                1 => "j",
                2 => iterVar ?? "i",
                3 => _random.Next(-100, 100).ToString(CultureInfo.InvariantCulture),
                4 => long.MinValue.ToString(CultureInfo.InvariantCulture),
                _ => long.MaxValue.ToString(CultureInfo.InvariantCulture),
            };
        }

        return _random.Next(8) switch
        {
            0 => $"({GenInt(depth + 1, iterVar, false)} + {GenInt(depth + 1, iterVar, true)})",
            1 => $"({GenInt(depth + 1, iterVar, false)} - {GenInt(depth + 1, iterVar, true)})",
            2 => $"({GenInt(depth + 1, iterVar, false)} * {GenInt(depth + 1, iterVar, true)})",
            3 => $"({GenInt(depth + 1, iterVar, false)} / {GenInt(depth + 1, iterVar, true)})",
            4 => $"({GenInt(depth + 1, iterVar, false)} % {GenInt(depth + 1, iterVar, true)})",
            5 => $"(-{GenInt(depth + 1, iterVar, true)})",
            6 => $"size({GenListInt(depth + 1, iterVar, true)})",
            _ => $"int({GenDouble(depth + 1, iterVar, true)})",
        };
    }

    private string GenUint(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(3) switch
            {
                0 => "u",
                1 => $"{(uint)_random.Next(0, 1000)}u",
                _ => "18446744073709551615u",
            };
        }

        return _random.Next(4) switch
        {
            0 => $"({GenUint(depth + 1, iterVar, false)} + {GenUint(depth + 1, iterVar, true)})",
            1 => $"({GenUint(depth + 1, iterVar, false)} * {GenUint(depth + 1, iterVar, true)})",
            2 => $"({GenUint(depth + 1, iterVar, false)} / {GenUint(depth + 1, iterVar, true)})",
            _ => $"uint({GenInt(depth + 1, iterVar, true)})",
        };
    }

    private string GenDouble(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(8) switch
            {
                0 => "d",
                1 => (_random.NextDouble() * 200 - 100).ToString("R", CultureInfo.InvariantCulture),
                2 => "0.0",
                3 => "1.5",
                4 => "3.14159",
                // Large/small magnitudes exercise Go's 'g' scientific-notation threshold.
                5 => (_random.NextDouble() * 1e24).ToString("R", CultureInfo.InvariantCulture),
                6 => (_random.NextDouble() * 1e-8).ToString("R", CultureInfo.InvariantCulture),
                _ => $"{_random.Next(1, 999)}e{_random.Next(-12, 12)}",
            };
        }

        return _random.Next(5) switch
        {
            0 => $"({GenDouble(depth + 1, iterVar, false)} + {GenDouble(depth + 1, iterVar, true)})",
            1 => $"({GenDouble(depth + 1, iterVar, false)} - {GenDouble(depth + 1, iterVar, true)})",
            2 => $"({GenDouble(depth + 1, iterVar, false)} * {GenDouble(depth + 1, iterVar, true)})",
            3 => $"({GenDouble(depth + 1, iterVar, false)} / {GenDouble(depth + 1, iterVar, true)})",
            _ => $"double({GenInt(depth + 1, iterVar, true)})",
        };
    }

    private string GenBool(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(3) switch { 0 => "b", 1 => "true", _ => "false" };
        }

        return _random.Next(12) switch
        {
            // Cross-type numeric comparisons — a rich source of subtle divergence.
            0 => $"({GenNumeric(depth, iterVar)} < {GenNumeric(depth, iterVar)})",
            1 => $"({GenNumeric(depth, iterVar)} <= {GenNumeric(depth, iterVar)})",
            2 => $"({GenNumeric(depth, iterVar)} > {GenNumeric(depth, iterVar)})",
            3 => $"({GenNumeric(depth, iterVar)} == {GenNumeric(depth, iterVar)})",
            4 => $"({GenNumeric(depth, iterVar)} != {GenNumeric(depth, iterVar)})",
            5 => $"({GenBool(depth + 1, iterVar, false)} && {GenBool(depth + 1, iterVar, true)})",
            6 => $"({GenBool(depth + 1, iterVar, false)} || {GenBool(depth + 1, iterVar, true)})",
            7 => $"(!{GenBool(depth + 1, iterVar, true)})",
            8 => $"({GenInt(depth + 1, iterVar, true)} in {GenListInt(depth + 1, iterVar, true)})",
            9 => $"{GenString(depth + 1, iterVar, true)}.contains({GenString(depth + 1, iterVar, true)})",
            10 => $"{GenString(depth + 1, iterVar, true)}.startsWith({GenString(depth + 1, iterVar, true)})",
            _ => GenComprehensionBool(depth, iterVar),
        };
    }

    private string GenNumeric(int depth, string? iterVar) => _random.Next(3) switch
    {
        0 => GenInt(depth + 1, iterVar, true),
        1 => GenUint(depth + 1, iterVar, true),
        _ => GenDouble(depth + 1, iterVar, true),
    };

    private string GenComprehensionBool(int depth, string? iterVar)
    {
        var v = "x" + depth; // unique per nesting level
        var list = GenListInt(depth + 1, iterVar, true);
        return _random.Next(3) switch
        {
            0 => $"{list}.all({v}, {GenBool(depth + 1, v, false)})",
            1 => $"{list}.exists({v}, {GenBool(depth + 1, v, false)})",
            _ => $"{list}.exists_one({v}, {GenBool(depth + 1, v, false)})",
        };
    }

    private string GenString(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(4) switch
            {
                0 => "s",
                1 => "''",
                2 => "'abc'",
                _ => Quote(RandomText()),
            };
        }

        return _random.Next(9) switch
        {
            0 => $"({GenString(depth + 1, iterVar, false)} + {GenString(depth + 1, iterVar, true)})",
            1 => $"string({GenInt(depth + 1, iterVar, true)})",
            2 => $"string({GenBool(depth + 1, iterVar, true)})",
            // string(double) — where a large/small-magnitude formatting bug once hid.
            3 => $"string({GenDouble(depth + 1, iterVar, true)})",
            // strings extension functions.
            4 => $"{GenString(depth + 1, iterVar, true)}.substring({GenSmallIndex()})",
            5 => $"{GenString(depth + 1, iterVar, true)}.replace({GenString(depth + 1, iterVar, true)}, {GenString(depth + 1, iterVar, true)})",
            6 => $"{GenString(depth + 1, iterVar, true)}.lowerAscii()",
            7 => $"{GenString(depth + 1, iterVar, true)}.upperAscii()",
            _ => $"[{GenString(depth + 1, iterVar, true)}, {GenString(depth + 1, iterVar, true)}].join('-')",
        };
    }

    private string GenSmallIndex() => _random.Next(0, 6).ToString(CultureInfo.InvariantCulture);

    private string GenListInt(int depth, string? iterVar, bool atom)
    {
        if (atom)
        {
            return _random.Next(3) switch
            {
                0 => "li",
                1 => "[]",
                _ => $"[{GenInt(depth + 1, iterVar, true)}, {GenInt(depth + 1, iterVar, true)}]",
            };
        }

        var v = "y" + depth;
        return _random.Next(3) switch
        {
            0 => $"{GenListInt(depth + 1, iterVar, true)}.map({v}, {GenInt(depth + 1, v, false)})",
            1 => $"{GenListInt(depth + 1, iterVar, true)}.filter({v}, {GenBool(depth + 1, v, false)})",
            _ => $"({GenListInt(depth + 1, iterVar, true)} + {GenListInt(depth + 1, iterVar, true)})",
        };
    }

    private string RandomText()
    {
        var len = _random.Next(0, 4);
        var sb = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            sb.Append((char)('a' + _random.Next(0, 26)));
        }

        return sb.ToString();
    }

    private static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
