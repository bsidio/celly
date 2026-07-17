// Independent conformance audit — mirrors the accusatory report's methodology but configured
// correctly. Data: textprotos converted to .binpb by Python text_format (their step 1). Celly:
// the 1.1.0 NuGet package. Config: ALL extension libraries + protobuf type support enabled, as
// every CEL conformance runner (cel-go/cel-java) does. Reports the real pass rate.
using Cel.Expr;
using Cel.Expr.Conformance.Test;
using Celly;
using Celly.Checking;
using Celly.Extensions;
using Celly.Interpreter;
using Celly.Protobuf;
using Celly.Types;
using Celly.Values;
using Google.Protobuf;

var binpbDir = args[0];

var registry = ProtoTypeRegistry.FromFiles(
    Cel.Expr.Conformance.Proto2.TestAllTypesReflection.Descriptor,
    Cel.Expr.Conformance.Proto2.TestAllTypesExtensionsReflection.Descriptor,
    Cel.Expr.Conformance.Proto3.TestAllTypesReflection.Descriptor);
var strongRegistry = ProtoTypeRegistry.FromFiles(true,
    Cel.Expr.Conformance.Proto2.TestAllTypesReflection.Descriptor,
    Cel.Expr.Conformance.Proto2.TestAllTypesExtensionsReflection.Descriptor,
    Cel.Expr.Conformance.Proto3.TestAllTypesReflection.Descriptor);
var strongEnumLib = strongRegistry.CreateEnumConversionLibrary();

CelLibrary[] Libraries(bool strong) =>
[
    .. strong ? new[] { strongEnumLib } : System.Array.Empty<CelLibrary>(),
    OptionalsLibrary.Instance, BindingsLibrary.Instance, BlockLibrary.Instance,
    EncodersLibrary.Instance, ProtosLibrary.Instance, TwoVarComprehensionsLibrary.Instance,
    MathLibrary.Instance, StringsLibrary.Instance, NetworkLibrary.Instance,
];

int total = 0, pass = 0, fail = 0;
var byFile = new SortedDictionary<string, (int p, int t)>();
var failures = new List<string>();

foreach (var path in Directory.EnumerateFiles(binpbDir, "*.binpb").OrderBy(p => p))
{
    var fileName = Path.GetFileNameWithoutExtension(path);
    var file = SimpleTestFile.Parser.ParseFrom(File.ReadAllBytes(path));
    foreach (var section in file.Section)
    {
        var strong = section.Name.StartsWith("strong_");
        foreach (var test in section.Test)
        {
            total++;
            var (p, tf) = byFile.GetValueOrDefault(fileName, (0, 0));
            var ok = RunOne(test, strong);
            byFile[fileName] = (p + (ok ? 1 : 0), tf + 1);
            if (ok) { pass++; }
            else { fail++; if (failures.Count < 30) failures.Add($"{fileName}/{section.Name}/{test.Name}"); }
        }
    }
}

Console.WriteLine($"\n=== INDEPENDENT AUDIT (Python text_format data + Celly 1.1.0 from NuGet + all extensions) ===");
Console.WriteLine($"PASS {pass} / {total}  ({100.0 * pass / total:F1}%)   FAIL {fail}\n");
foreach (var (f, (p, t)) in byFile)
    Console.WriteLine($"  {f,-16} {p,4}/{t,-4} {(p == t ? "" : "  <-- " + (t - p) + " fail")}");
if (failures.Count > 0)
{
    Console.WriteLine("\nfirst failures:");
    foreach (var f in failures) Console.WriteLine("  " + f);
}
return fail == 0 ? 0 : 1;

bool RunOne(SimpleTest test, bool strong)
{
    try
    {
        var reg = strong ? strongRegistry : registry;
        var env = CelEnv.Create(new CelEnvSettings
        {
            Container = test.Container,
            DisableMacros = test.DisableMacros,
            TypeProvider = reg,
            Adapter = reg,
            Libraries = Libraries(strong),
            Declarations = [.. test.TypeEnv.Where(d => d.DeclKindCase == Decl.DeclKindOneofCase.Ident)
                .Select(d => new VariableDecl(d.Name, TypeConverter.ToCelType(d.Ident.Type)))],
            FunctionDeclarations = [.. test.TypeEnv.Where(d => d.DeclKindCase == Decl.DeclKindOneofCase.Function)
                .Select(d => new FunctionDecl(d.Name, [.. d.Function.Overloads.Select(o =>
                    new OverloadDecl(o.OverloadId, [.. o.Params.Select(TypeConverter.ToCelType)],
                        TypeConverter.ToCelType(o.ResultType), o.IsInstanceFunction))]))],
        });

        var parsed = env.Parse(test.Expr);
        bool expectsErr = test.ResultMatcherCase is SimpleTest.ResultMatcherOneofCase.EvalError
            or SimpleTest.ResultMatcherOneofCase.AnyEvalErrors;
        if (parsed.Ast is null) return expectsErr;

        if (!test.DisableCheck)
        {
            var chk = env.Check(parsed.Ast);
            if (chk.HasErrors) return expectsErr;
            if (test.ResultMatcherCase == SimpleTest.ResultMatcherOneofCase.TypedResult
                && test.TypedResult.DeducedType is { } dt)
            {
                if (!TypeSubstitution.StructuralEquals(TypeConverter.ToCelType(dt), parsed.Ast.TypeMap![parsed.Ast.Expr.Id]))
                    return false;
            }
        }
        if (test.CheckOnly) return true;

        IActivation act = EmptyActivation.Instance;
        if (test.Bindings.Count > 0)
        {
            var b = new Dictionary<string, CelValue>();
            foreach (var (n, ev) in test.Bindings) b[n] = ValueConverter.ToCelValue(ev.Value, reg);
            act = new ValueActivation(b);
        }
        var result = env.Program(parsed.Ast).Eval(act);

        return test.ResultMatcherCase switch
        {
            SimpleTest.ResultMatcherOneofCase.Value => Match(ValueConverter.ToCelValue(test.Value, reg), result),
            SimpleTest.ResultMatcherOneofCase.TypedResult => test.TypedResult.Result is null || Match(ValueConverter.ToCelValue(test.TypedResult.Result, reg), result),
            SimpleTest.ResultMatcherOneofCase.EvalError or SimpleTest.ResultMatcherOneofCase.AnyEvalErrors => result is ErrorValue,
            SimpleTest.ResultMatcherOneofCase.Unknown or SimpleTest.ResultMatcherOneofCase.AnyUnknowns => result is UnknownValue,
            _ => Match(BoolValue.True, result),
        };
    }
    catch { return false; }
}

bool Match(CelValue e, CelValue a) => (e, a) switch
{
    (NullValue, NullValue) => true,
    (BoolValue x, BoolValue y) => x.Value == y.Value,
    (IntValue x, IntValue y) => x.Value == y.Value,
    (UintValue x, UintValue y) => x.Value == y.Value,
    (DoubleValue x, DoubleValue y) => (double.IsNaN(x.Value) && double.IsNaN(y.Value)) || System.BitConverter.DoubleToInt64Bits(x.Value) == System.BitConverter.DoubleToInt64Bits(y.Value),
    (StringValue x, StringValue y) => x.Value == y.Value,
    (BytesValue x, BytesValue y) => x.Span.SequenceEqual(y.Span),
    (TypeValue x, TypeValue y) => x.Value.Name == y.Value.Name,
    (TimestampValue x, TimestampValue y) => x.Data == y.Data,
    (DurationValue x, DurationValue y) => x.Data == y.Data,
    (Celly.Values.EnumValue x, Celly.Values.EnumValue y) => x.EnumType.Name == y.EnumType.Name && x.Number == y.Number,
    (Celly.Values.ListValue x, Celly.Values.ListValue y) => x.Elements.Count == y.Elements.Count && x.Elements.Zip(y.Elements).All(p => Match(p.First, p.Second)),
    (Celly.Values.MapValue x, Celly.Values.MapValue y) => MatchMap(x, y),
    (ProtoMessageValue x, ProtoMessageValue y) => x.EqualTo(y) || x.Message.Equals(y.Message),
    _ => false,
};

bool MatchMap(Celly.Values.MapValue x, Celly.Values.MapValue y)
{
    if (x.Count != y.Count) return false;
    foreach (var k in x.Keys)
    {
        var m = y.Keys.FirstOrDefault(yk => Match(k, yk));
        if (m is null) return false;
        x.TryGet(k, out var xv); y.TryGet(m, out var yv);
        if (!Match(xv, yv)) return false;
    }
    return true;
}
