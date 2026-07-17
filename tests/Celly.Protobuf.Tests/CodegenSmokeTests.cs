using Cel.Expr.Conformance.Proto2;
using Cel.Expr.Conformance.Proto3;
using Google.Protobuf;
using Xunit;

namespace Celly.Protobuf.Tests;

/// <summary>
/// Validates that the C# codegen of the cel-spec TestAllTypes protos (proto2 incl. groups and
/// extensions, and proto3) is usable — de-risking conformance work before implementation starts.
/// </summary>
public class CodegenSmokeTests
{
    [Fact]
    public void Proto3TestAllTypesRoundTrips()
    {
        var msg = new Cel.Expr.Conformance.Proto3.TestAllTypes
        {
            SingleInt64 = 42,
            SingleString = "hello",
        };
        var parsed = Cel.Expr.Conformance.Proto3.TestAllTypes.Parser.ParseFrom(msg.ToByteArray());
        Assert.Equal(42, parsed.SingleInt64);
        Assert.Equal("hello", parsed.SingleString);
    }

    [Fact]
    public void Proto2TestAllTypesRoundTrips()
    {
        var msg = new Cel.Expr.Conformance.Proto2.TestAllTypes
        {
            SingleInt32 = 7,
        };
        var parsed = Cel.Expr.Conformance.Proto2.TestAllTypes.Parser.ParseFrom(msg.ToByteArray());
        Assert.Equal(7, parsed.SingleInt32);
        Assert.True(parsed.HasSingleInt32);
    }

    [Fact]
    public void Proto2ExtensionsAreGenerated()
    {
        var msg = new Cel.Expr.Conformance.Proto2.TestAllTypes();
        msg.SetExtension(TestAllTypesExtensionsExtensions.Int32Ext, 99);
        Assert.Equal(99, msg.GetExtension(TestAllTypesExtensionsExtensions.Int32Ext));
    }
}
