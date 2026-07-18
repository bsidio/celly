// protovalidate conformance executor.
//
// Speaks buf's conformance protocol: reads a TestConformanceRequest (binary proto) from stdin,
// validates each case message, writes a TestConformanceResponse to stdout. Driven by the official
// `protovalidate-conformance` runner:
//
//   protovalidate-conformance -- dotnet run --project tests/Celly.Protovalidate.Conformance
//
// The conformance case types are generated into this assembly, so we resolve each case's Any
// against a TypeRegistry of all loaded proto descriptors and parse it to a concrete IMessage —
// no dynamic-message support required.

using System.Reflection;
using Buf.Validate;
using Buf.Validate.Conformance.Harness;
using Celly.Protovalidate;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

// Gather every generated FileDescriptor in the loaded assemblies (cases, harness, validate, WKTs)
// so Any type_urls resolve to concrete parsers, and the validator understands every case type.
var descriptors = AllFileDescriptors().ToArray();
var registry = TypeRegistry.FromFiles(descriptors);
var validator = new Validator(descriptors);

var request = TestConformanceRequest.Parser.ParseFrom(Console.OpenStandardInput());
var response = new TestConformanceResponse();

foreach (var (name, any) in request.Cases)
{
    response.Results[name] = RunCase(any, registry, validator);
}

using var stdout = Console.OpenStandardOutput();
response.WriteTo(stdout);
stdout.Flush();
return;

static TestResult RunCase(Any any, TypeRegistry registry, Validator validator)
{
    IMessage message;
    try
    {
        var typeName = any.TypeUrl[(any.TypeUrl.LastIndexOf('/') + 1)..];
        var descriptor = registry.Find(typeName);
        if (descriptor is null)
        {
            return new TestResult { RuntimeError = $"unknown type: {typeName}" };
        }

        message = descriptor.Parser.ParseFrom(any.Value);
    }
    catch (Exception ex)
    {
        return new TestResult { RuntimeError = $"parse failed: {ex.Message}" };
    }

    try
    {
        var violations = validator.Validate(message);
        return violations.Count == 0
            ? new TestResult { Success = true }
            : new TestResult { ValidationError = new Violations { Violations_ = { violations } } };
    }
    catch (ValidationCompileException ex)
    {
        return new TestResult { CompilationError = ex.Message };
    }
    catch (ValidationException ex)
    {
        return new TestResult { RuntimeError = ex.Message };
    }
    catch (Exception ex)
    {
        return new TestResult { RuntimeError = ex.Message };
    }
}

static IEnumerable<FileDescriptor> AllFileDescriptors()
{
    var seen = new HashSet<string>();
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        System.Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t is not null).ToArray()!;
        }

        // Generated proto files expose a static `Descriptor` (FileDescriptor) on a *Reflection class.
        foreach (var type in types.Where(t => t.Name.EndsWith("Reflection", StringComparison.Ordinal)))
        {
            var prop = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is FileDescriptor fd && seen.Add(fd.Name))
            {
                yield return fd;
            }
        }
    }
}
