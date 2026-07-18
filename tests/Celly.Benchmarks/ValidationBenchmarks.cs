using BenchmarkDotNet.Attributes;
using Celly.Protovalidate;
using Celly.Protovalidate.Tests;

namespace Celly.Benchmarks;

/// <summary>
/// protovalidate validation cost: a validator is built once (rules compiled), then a message is
/// validated repeatedly — the policy-engine steady state that matters in production.
/// </summary>
[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private Validator _validator = null!;
    private Person _valid = null!;
    private Person _invalid = null!;

    [GlobalSetup]
    public void Setup()
    {
        _validator = new Validator(Person.Descriptor.File);
        _valid = new Person
        {
            Name = "Ada", Email = "ada@example.com", Age = 36, Roles = { "admin" }, Scores = { ["math"] = 100 },
        };
        _invalid = new Person { Name = "", Email = "bad", Age = 999 };
    }

    // Building the validator (parsing + compiling every rule's CEL). Paid once per message type.
    [Benchmark]
    public Validator Build() => new(Person.Descriptor.File);

    [Benchmark]
    public int Validate_Valid() => _validator.Validate(_valid).Count;

    [Benchmark]
    public int Validate_Invalid() => _validator.Validate(_invalid).Count;
}
