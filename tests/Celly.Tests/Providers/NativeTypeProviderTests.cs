using Celly.Checking;
using Celly.Providers;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Providers;

// Test model: a mix of records, mutable classes, enums, nullables, collections, and nesting.
public enum Tier
{
    Free = 0,
    Pro = 1,
    Enterprise = 2,
}

public sealed record Address(string City, string Country);

public sealed class User
{
    public string Name { get; set; } = string.Empty;

    public long Age { get; set; }

    public double Score { get; set; }

    public bool Active { get; set; }

    public Tier Tier { get; set; }

    public int? LoginCount { get; set; }          // Nullable<T> -> wrapper semantics

    public Address? HomeAddress { get; set; }

    public List<string> Roles { get; set; } = [];

    public Dictionary<string, long> Quotas { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public class NativeTypeProviderTests
{
    private static readonly NativeTypeProvider Provider = NativeTypeProvider.FromTypes(typeof(User));

    private static CelEnv MakeEnv() => CelEnv.Create(new CelEnvSettings
    {
        TypeProvider = Provider,
        Adapter = Provider,
        Declarations =
        [
            new VariableDecl("user", CelType.Struct("Celly.Tests.Providers.User")),
        ],
        Libraries = [Celly.Extensions.OptionalsLibrary.Instance],
    });

    private static CelValue Eval(string expression, User user)
    {
        var env = MakeEnv();
        var parsed = env.Parse(expression);
        Assert.NotNull(parsed.Ast);
        var check = env.Check(parsed.Ast!);
        Assert.False(check.HasErrors, string.Join("; ", check.Issues));
        return env.Program(parsed.Ast!).Eval(new Dictionary<string, object?> { ["user"] = user });
    }

    private static readonly User Amy = new()
    {
        Name = "amy",
        Age = 34,
        Score = 9.5,
        Active = true,
        Tier = Tier.Pro,
        LoginCount = 12,
        HomeAddress = new Address("Lisbon", "PT"),
        Roles = ["admin", "dev"],
        Quotas = new Dictionary<string, long> { ["api"] = 1000 },
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1234567890),
    };

    // ---- field access -----------------------------------------------------------------------------

    [Theory]
    [InlineData("user.Name == 'amy'")]                        // verbatim property name
    [InlineData("user.name == 'amy'")]                        // snake_case alias
    [InlineData("user.age + 1 == 35")]
    [InlineData("user.score > 9.0")]
    [InlineData("user.active")]
    [InlineData("user.tier == 1")]                            // enum reads as int
    [InlineData("user.login_count == 12")]                    // nullable with a value
    [InlineData("user.home_address.city == 'Lisbon'")]        // nested auto-registered record
    [InlineData("'admin' in user.roles")]
    [InlineData("user.roles.size() == 2")]
    [InlineData("user.quotas['api'] == 1000")]
    [InlineData("user.created_at.getFullYear() == 2009")]     // DateTimeOffset -> timestamp
    [InlineData("user.created_at < timestamp('2020-01-01T00:00:00Z')")]
    public void FieldAccess(string expression, bool expected = true) =>
        Assert.Equal(expected, Assert.IsType<BoolValue>(Eval(expression, Amy)).Value);

    [Fact]
    public void EnumConstantsResolve() =>
        Assert.True(Assert.IsType<BoolValue>(Eval("user.tier == Celly.Tests.Providers.Tier.Pro", Amy)).Value);

    [Fact]
    public void NullableWithoutValueReadsNull()
    {
        var user = new User { Name = "bo" };
        Assert.True(Assert.IsType<BoolValue>(Eval("user.login_count == null", user)).Value);
        Assert.Equal(5L, Assert.IsType<IntValue>(Eval("user.?login_count.orValue(5)", user)).Value);
    }

    // ---- has() presence ---------------------------------------------------------------------------

    [Theory]
    [InlineData("has(user.name)", true)]
    [InlineData("has(user.login_count)", true)]
    [InlineData("has(user.roles)", true)]
    [InlineData("has(user.home_address)", true)]
    public void HasOnSetFields(string expression, bool expected) =>
        Assert.Equal(expected, Assert.IsType<BoolValue>(Eval(expression, Amy)).Value);

    [Fact]
    public void HasOnDefaults()
    {
        var blank = new User();
        Assert.False(Assert.IsType<BoolValue>(Eval("has(user.name)", blank)).Value);
        Assert.False(Assert.IsType<BoolValue>(Eval("has(user.age)", blank)).Value);
        Assert.False(Assert.IsType<BoolValue>(Eval("has(user.login_count)", blank)).Value);
        Assert.False(Assert.IsType<BoolValue>(Eval("has(user.home_address)", blank)).Value);
        Assert.False(Assert.IsType<BoolValue>(Eval("has(user.roles)", blank)).Value);
    }

    // ---- construction -----------------------------------------------------------------------------

    [Fact]
    public void ConstructsMutableClass()
    {
        var result = Eval(
            "Celly.Tests.Providers.User{name: 'zoe', age: 3, roles: ['x'], login_count: 7}.name",
            Amy);
        Assert.Equal("zoe", Assert.IsType<StringValue>(result).Value);
    }

    [Fact]
    public void ConstructsRecordThroughPrimaryConstructor()
    {
        var result = Eval("Celly.Tests.Providers.Address{city: 'Porto', country: 'PT'}.city", Amy);
        Assert.Equal("Porto", Assert.IsType<StringValue>(result).Value);
    }

    [Fact]
    public void ConstructionWithContainer()
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Container = "Celly.Tests.Providers",   // now short names resolve
            TypeProvider = Provider,
            Adapter = Provider,
        });
        var program = env.Program(env.Parse("Address{city: 'Faro', country: 'PT'}").Ast!);
        var value = Assert.IsType<NativeTypeProvider.NativeObjectValue>(program.Eval());
        Assert.Equal("Faro", Assert.IsType<Address>(value.ToNative()).City);
    }

    [Fact]
    public void ConstructionErrors()
    {
        var env = CelEnv.Create(new CelEnvSettings { TypeProvider = Provider, Adapter = Provider });

        // Unknown field: caught by the CHECKER when checked…
        var parsed = env.Parse("Celly.Tests.Providers.User{bogus: 1}");
        Assert.True(env.Check(parsed.Ast!).HasErrors);

        // …and by the runtime when unchecked.
        var result = env.Program(parsed.Ast!).Eval();
        Assert.Contains("no_such_field", Assert.IsType<ErrorValue>(result).Message);

        // Type mismatch is a checker error too.
        var mismatch = env.Parse("Celly.Tests.Providers.User{age: 'old'}");
        Assert.True(env.Check(mismatch.Ast!).HasErrors);
    }

    [Fact]
    public void NullableFieldAcceptsNullInConstruction()
    {
        var result = Eval("Celly.Tests.Providers.User{name: 'n', login_count: null}.login_count == null", Amy);
        Assert.True(Assert.IsType<BoolValue>(result).Value);
    }

    // ---- checker integration ----------------------------------------------------------------------

    [Fact]
    public void CheckerSeesPropertyTypes()
    {
        var env = MakeEnv();
        var ast = env.Parse("user.age").Ast!;
        var check = env.Check(ast);
        Assert.False(check.HasErrors);
        Assert.Equal(CelTypeKind.Int, check.TypeOf(ast.Expr).Kind);

        // Bad field usage is rejected at check time.
        var bad = env.Parse("user.name + 1").Ast!;
        Assert.True(env.Check(bad).HasErrors);

        var wrapper = env.Parse("user.login_count").Ast!;
        var wrapperCheck = env.Check(wrapper);
        Assert.False(wrapperCheck.HasErrors);
        Assert.True(TypeSubstitution.IsWrapper(wrapperCheck.TypeOf(wrapper.Expr)));
    }

    // ---- equality ---------------------------------------------------------------------------------

    [Fact]
    public void FieldwiseEquality()
    {
        var expr = "Celly.Tests.Providers.Address{city: 'A', country: 'B'} == Celly.Tests.Providers.Address{city: 'A', country: 'B'}";
        Assert.True(Assert.IsType<BoolValue>(Eval(expr, Amy)).Value);

        var different = "Celly.Tests.Providers.Address{city: 'A', country: 'B'} == Celly.Tests.Providers.Address{city: 'A', country: 'C'}";
        Assert.False(Assert.IsType<BoolValue>(Eval(different, Amy)).Value);
    }

    // ---- snake_case conversion ---------------------------------------------------------------------

    [Theory]
    [InlineData("FirstName", "first_name")]
    [InlineData("URL", "url")]
    [InlineData("HTTPServer", "http_server")]
    [InlineData("Age", "age")]
    [InlineData("already_snake", "already_snake")]
    public void SnakeCase(string input, string expected) =>
        Assert.Equal(expected, NativeTypeProvider.ToSnakeCase(input));
}
