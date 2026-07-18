using Celly.Protovalidate.Tests;
using Xunit;

namespace Celly.Protovalidate.Tests;

public class ValidatorTests
{
    private static readonly Validator Validator = new(Person.Descriptor.File);

    private static Person Valid() => new()
    {
        Name = "Ada",
        Email = "ada@example.com",
        Age = 36,
        Roles = { "admin" },
        Scores = { ["math"] = 100 },
    };

    private static IReadOnlyList<string> RuleIds(Google.Protobuf.IMessage message) =>
        Validator.Validate(message).Select(v => v.RuleId).ToList();

    [Fact]
    public void Valid_message_has_no_violations() => Assert.Empty(Validator.Validate(Valid()));

    [Fact]
    public void Missing_name_violates_min_len()
    {
        var p = Valid();
        p.Name = "";
        Assert.Contains("string.min_len", RuleIds(p));
    }

    [Fact]
    public void Invalid_email_violates_email_rule()
    {
        var p = Valid();
        p.Email = "not-an-email";
        Assert.Contains("string.email", RuleIds(p));
    }

    [Fact]
    public void Age_out_of_range_violates_lte()
    {
        var p = Valid();
        p.Age = 200;
        Assert.Contains("int32.gte_lte", RuleIds(p));
    }

    [Fact]
    public void Empty_repeated_violates_min_items()
    {
        var p = Valid();
        p.Roles.Clear();
        Assert.Contains("repeated.min_items", RuleIds(p));
    }

    [Fact]
    public void Negative_map_value_violates_values_rule()
    {
        var p = Valid();
        p.Scores["math"] = -1;
        var violation = Assert.Single(Validator.Validate(p));
        Assert.Equal("int32.gte", violation.RuleId);
        Assert.NotNull(violation.Field);
        Assert.True(violation.Field.Elements[^1].HasStringKey); // val[key] subscript on the map field
    }

    [Fact]
    public void Message_level_rule_reported_by_id()
    {
        var p = Valid();
        p.Age = 30;
        p.Email = ""; // adult without email → message rule fires
        Assert.Contains("adult_needs_email", RuleIds(p));
    }

    [Fact]
    public void All_violations_are_collected()
    {
        var p = new Person { Name = "", Email = "bad", Age = 999 }; // name, email, age, roles all invalid
        var ids = RuleIds(p);
        Assert.Contains("string.min_len", ids);
        Assert.Contains("string.email", ids);
        Assert.Contains("int32.gte_lte", ids);
        Assert.Contains("repeated.min_items", ids);
    }

    [Fact]
    public void Validator_is_safe_for_concurrent_use()
    {
        var valid = Valid();
        var invalid = new Person { Name = "", Age = 200 };
        Parallel.For(0, 2000, i =>
        {
            if (i % 2 == 0)
            {
                Assert.Empty(Validator.Validate(valid));
            }
            else
            {
                Assert.NotEmpty(Validator.Validate(invalid));
            }
        });
    }
}
