using System.Reflection;
using System.Text;
using Xunit;

namespace Celly.Tests;

/// <summary>
/// Snapshots the entire public API surface of the Celly assembly and compares it to a checked-in
/// baseline (<c>PublicApi.Celly.txt</c>). Any addition, removal, or signature change to a public
/// member fails this test — the guard against silent breaking changes now that the API is frozen.
///
/// To intentionally change the API: run with <c>CELLY_UPDATE_PUBLIC_API=1</c> to rewrite the
/// baseline, review the diff, and commit it alongside the code change.
/// </summary>
public class PublicApiTests
{
    [Fact]
    public void CellyPublicApiMatchesBaseline() =>
        AssertApi(typeof(CelEnv).Assembly, "PublicApi.Celly.txt");

    private static void AssertApi(Assembly assembly, string baselineFileName)
    {
        var actual = DescribeAssembly(assembly);
        var baselinePath = LocateBaseline(baselineFileName);

        if (Environment.GetEnvironmentVariable("CELLY_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(baselinePath, actual);
            return;
        }

        var baseline = File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : string.Empty;
        if (baseline.ReplaceLineEndings() != actual.ReplaceLineEndings())
        {
            Assert.Fail(
                $"Public API of {assembly.GetName().Name} differs from the baseline ({baselineFileName}).\n" +
                "If this change is intentional, regenerate with CELLY_UPDATE_PUBLIC_API=1 and commit the diff.\n\n" +
                FirstDifferences(baseline, actual));
        }
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add(DescribeType(type));
            foreach (var member in type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(IsApiMember)
                .Select(DescribeMember)
                .OrderBy(s => s, StringComparer.Ordinal))
            {
                lines.Add("    " + member);
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    private static bool IsApiMember(MemberInfo member)
    {
        // Skip compiler-generated accessors and object overrides that add no API meaning.
        if (member is MethodInfo { IsSpecialName: false } method)
        {
            return method.Name is not ("GetHashCode" or "Equals" or "ToString" or "GetType");
        }

        return member is PropertyInfo or FieldInfo or ConstructorInfo or EventInfo or Type
            || (member is MethodInfo m && m.IsSpecialName && (m.Name.StartsWith("op_") || m.Name.StartsWith("get_") || m.Name.StartsWith("set_")));
    }

    private static string DescribeType(Type type)
    {
        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct"
            : type.IsAbstract && type.IsSealed ? "static class" : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class" : "class";
        var bases = new List<string>();
        if (type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum))
        {
            bases.Add(TypeName(b));
        }

        bases.AddRange(type.GetInterfaces().Where(i => i.IsPublic).Select(TypeName).OrderBy(s => s, StringComparer.Ordinal));
        var suffix = bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty;
        return $"{kind} {type.FullName}{suffix}";
    }

    private static string DescribeMember(MemberInfo member) => member switch
    {
        ConstructorInfo c => $".ctor({Parameters(c.GetParameters())})",
        PropertyInfo p => $"{TypeName(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}",
        FieldInfo f => $"{(f.IsLiteral ? "const" : f.IsInitOnly ? "readonly" : "field")} {TypeName(f.FieldType)} {f.Name}",
        MethodInfo m => $"{TypeName(m.ReturnType)} {m.Name}{Generics(m)}({Parameters(m.GetParameters())})",
        EventInfo e => $"event {TypeName(e.EventHandlerType!)} {e.Name}",
        _ => member.ToString() ?? member.Name,
    };

    private static string Generics(MethodInfo m) =>
        m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    private static string Parameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return "ref " + TypeName(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }

            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }

        return type.FullName ?? type.Name;
    }

    private static string FirstDifferences(string baseline, string actual)
    {
        var baseLines = baseline.ReplaceLineEndings("\n").Split('\n');
        var actualLines = actual.ReplaceLineEndings("\n").Split('\n');
        var baseSet = new HashSet<string>(baseLines);
        var actualSet = new HashSet<string>(actualLines);
        var sb = new StringBuilder();
        foreach (var added in actualLines.Where(l => !baseSet.Contains(l) && l.Length > 0).Take(25))
        {
            sb.AppendLine("  + " + added.Trim());
        }

        foreach (var removed in baseLines.Where(l => !actualSet.Contains(l) && l.Length > 0).Take(25))
        {
            sb.AppendLine("  - " + removed.Trim());
        }

        return sb.ToString();
    }

    private static string LocateBaseline(string fileName)
    {
        // Walk up from the test output dir to the repo, landing the baseline next to the tests.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Celly.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "ApiBaselines", fileName);
    }
}
