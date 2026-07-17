using System.Net;
using System.Net.Sockets;
using Celly.Checking;
using Celly.Types;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>An IP address value (net.IP opaque type).</summary>
public sealed class IpValue(IPAddress address) : CelValue
{
    public static readonly CelType IpType = CelType.Opaque("net.IP");

    public IPAddress Address { get; } = address;

    public override CelType Type => IpType;

    public override bool EqualTo(CelValue other) => other is IpValue ip && ip.Address.Equals(Address);

    public override object ToNative() => Address;

    public override string ToString() => Address.ToString();
}

/// <summary>A CIDR network value (net.CIDR opaque type): address + prefix length.</summary>
public sealed class CidrValue(IPAddress address, int prefixLength) : CelValue
{
    public static readonly CelType CidrType = CelType.Opaque("net.CIDR");

    public IPAddress Address { get; } = address;

    public int PrefixLength { get; } = prefixLength;

    public override CelType Type => CidrType;

    public override bool EqualTo(CelValue other) =>
        other is CidrValue cidr && cidr.Address.Equals(Address) && cidr.PrefixLength == PrefixLength;

    public override object ToNative() => (Address, PrefixLength);

    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>The networking extension: ip()/cidr() parsing, classification, and containment.</summary>
public static class NetworkLibrary
{
    public static readonly CelLibrary Instance = new()
    {
        Name = "network",
        Functions = Register,
        FunctionDecls =
        [
            new FunctionDecl("ip", [new OverloadDecl("string_to_ip", [CelType.String], IpValue.IpType)]),
            new FunctionDecl("isIP", [new OverloadDecl("is_ip_string", [CelType.String], CelType.Bool)]),
            new FunctionDecl("ip.isCanonical", [new OverloadDecl("ip_is_canonical", [CelType.String], CelType.Bool)]),
            new FunctionDecl("cidr", [new OverloadDecl("string_to_cidr", [CelType.String], CidrValue.CidrType)]),
            new FunctionDecl("isCIDR", [new OverloadDecl("is_cidr_string", [CelType.String], CelType.Bool)]),
            new FunctionDecl("family", [new OverloadDecl("ip_family", [IpValue.IpType], CelType.Int, isInstance: true)]),
            new FunctionDecl("isUnspecified", [new OverloadDecl("ip_is_unspecified", [IpValue.IpType], CelType.Bool, isInstance: true)]),
            new FunctionDecl("isLoopback", [new OverloadDecl("ip_is_loopback", [IpValue.IpType], CelType.Bool, isInstance: true)]),
            new FunctionDecl("isLinkLocalMulticast", [new OverloadDecl("ip_is_ll_multicast", [IpValue.IpType], CelType.Bool, isInstance: true)]),
            new FunctionDecl("isLinkLocalUnicast", [new OverloadDecl("ip_is_ll_unicast", [IpValue.IpType], CelType.Bool, isInstance: true)]),
            new FunctionDecl("isGlobalUnicast", [new OverloadDecl("ip_is_global_unicast", [IpValue.IpType], CelType.Bool, isInstance: true)]),
            new FunctionDecl("containsIP",
            [
                new OverloadDecl("cidr_contains_ip", [CidrValue.CidrType, IpValue.IpType], CelType.Bool, isInstance: true),
                new OverloadDecl("cidr_contains_ip_string", [CidrValue.CidrType, CelType.String], CelType.Bool, isInstance: true),
            ]),
            new FunctionDecl("containsCIDR",
            [
                new OverloadDecl("cidr_contains_cidr", [CidrValue.CidrType, CidrValue.CidrType], CelType.Bool, isInstance: true),
                new OverloadDecl("cidr_contains_cidr_string", [CidrValue.CidrType, CelType.String], CelType.Bool, isInstance: true),
            ]),
            new FunctionDecl("ip", [new OverloadDecl("cidr_ip", [CidrValue.CidrType], IpValue.IpType, isInstance: true)]),
            new FunctionDecl("masked", [new OverloadDecl("cidr_masked", [CidrValue.CidrType], CidrValue.CidrType, isInstance: true)]),
            new FunctionDecl("prefixLength", [new OverloadDecl("cidr_prefix_length", [CidrValue.CidrType], CelType.Int, isInstance: true)]),
            new FunctionDecl("string", [new OverloadDecl("ip_to_string", [IpValue.IpType], CelType.String),
                new OverloadDecl("cidr_to_string", [CidrValue.CidrType], CelType.String)]),
        ],
        VariableDecls =
        [
            new VariableDecl("net.IP", new CelType(CelTypeKind.Type, "type", [IpValue.IpType])),
            new VariableDecl("net.CIDR", new CelType(CelTypeKind.Type, "type", [CidrValue.CidrType])),
        ],
    };

    private static void Register(Stdlib.FunctionRegistry registry)
    {
        registry.Register("ip", args => args switch
        {
            [StringValue s] => TryParseIp(s.Value) is { } ip ? new IpValue(ip) : new ErrorValue($"invalid IP address: {s.Value}"),
            [CidrValue cidr] => new IpValue(cidr.Address),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("isIP", args => args is [StringValue s]
            ? BoolValue.Of(TryParseIp(s.Value) is not null)
            : ErrorValue.NoSuchOverload());
        registry.Register("ip.isCanonical", args =>
        {
            if (args is not [StringValue s])
            {
                return ErrorValue.NoSuchOverload();
            }

            // Unparseable input is an error, not merely non-canonical.
            return TryParseIp(s.Value) is { } ip
                ? BoolValue.Of(ip.ToString() == s.Value)
                : new ErrorValue($"invalid IP address: {s.Value}");
        });

        registry.Register("cidr", args => args is [StringValue s]
            ? TryParseCidr(s.Value) is { } cidr ? cidr : new ErrorValue($"invalid CIDR: {s.Value}")
            : ErrorValue.NoSuchOverload());
        registry.Register("isCIDR", args => args is [StringValue s]
            ? BoolValue.Of(TryParseCidr(s.Value) is not null)
            : ErrorValue.NoSuchOverload());

        registry.Register("family", args => args is [IpValue ip]
            ? IntValue.Of(ip.Address.AddressFamily == AddressFamily.InterNetwork ? 4 : 6)
            : ErrorValue.NoSuchOverload());
        registry.Register("isUnspecified", args => Classify(args, ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));
        registry.Register("isLoopback", args => Classify(args, IPAddress.IsLoopback));
        registry.Register("isLinkLocalMulticast", args => Classify(args, IsLinkLocalMulticast));
        registry.Register("isLinkLocalUnicast", args => Classify(args, IsLinkLocalUnicast));
        registry.Register("isGlobalUnicast", args => Classify(args, ip =>
            !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !ip.Equals(IPAddress.Broadcast)
            && !IPAddress.IsLoopback(ip) && !IsMulticast(ip) && !IsLinkLocalUnicast(ip)));

        registry.Register("containsIP", args => args switch
        {
            [CidrValue cidr, IpValue ip] => ContainsIp(cidr, ip.Address),
            [CidrValue cidr, StringValue s] => TryParseIp(s.Value) is { } ip
                ? ContainsIp(cidr, ip)
                : new ErrorValue($"invalid IP address: {s.Value}"),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("containsCIDR", args => args switch
        {
            [CidrValue outer, CidrValue inner] => ContainsCidr(outer, inner),
            [CidrValue outer, StringValue s] => TryParseCidr(s.Value) is { } inner
                ? ContainsCidr(outer, inner)
                : new ErrorValue($"invalid CIDR: {s.Value}"),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("masked", args => args is [CidrValue cidr]
            ? new CidrValue(Mask(cidr.Address, cidr.PrefixLength), cidr.PrefixLength)
            : ErrorValue.NoSuchOverload());
        registry.Register("prefixLength", args => args is [CidrValue cidr]
            ? IntValue.Of(cidr.PrefixLength)
            : ErrorValue.NoSuchOverload());

        // string() gains IP/CIDR overloads; delegate everything else to the previous impl.
        var previousString = registry.Find("string")!;
        registry.Register("string", args => args switch
        {
            [IpValue ip] => StringValue.Of(ip.Address.ToString()),
            [CidrValue cidr] => StringValue.Of(cidr.ToString()),
            _ => previousString(args),
        });
    }

    private static IPAddress? TryParseIp(string s)
    {
        // Zone identifiers (fe80::1%en0) are not valid in this extension.
        if (s.Contains('/') || s.Contains('%') || s.Length == 0)
        {
            return null;
        }

        // Dotted-quad notation inside IPv6 text (::ffff:1.2.3.4) is rejected by this extension;
        // the hex-form IPv4-mapped address (::ffff:c0a8:1) is valid and normalizes to IPv4.
        if (s.Contains(':') && s.Contains('.'))
        {
            return null;
        }

        if (!IPAddress.TryParse(s, out var ip))
        {
            return null;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') != 3)
        {
            return null; // "192.168.1" style shorthand is not a valid textual IPv4 address
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4(); // mapped addresses ARE the IPv4 address (Go To4() semantics)
        }

        return ip;
    }

    private static CidrValue? TryParseCidr(string s)
    {
        var slash = s.LastIndexOf('/');
        if (slash <= 0 || slash == s.Length - 1)
        {
            return null;
        }

        var ip = TryParseIp(s[..slash]);
        if (ip is null)
        {
            return null;
        }

        if (!int.TryParse(s[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix))
        {
            return null;
        }

        var maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return prefix > maxPrefix ? null : new CidrValue(ip, prefix);
    }

    private static CelValue Classify(CelValue[] args, Func<IPAddress, bool> predicate) =>
        args is [IpValue ip] ? BoolValue.Of(predicate(ip.Address)) : ErrorValue.NoSuchOverload();

    private static bool IsMulticast(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? (bytes[0] & 0xF0) == 0xE0
            : bytes[0] == 0xFF;
    }

    private static bool IsLinkLocalMulticast(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 224 && bytes[1] == 0 && bytes[2] == 0
            : bytes[0] == 0xFF && (bytes[1] & 0x0F) == 0x02;
    }

    private static bool IsLinkLocalUnicast(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 169 && bytes[1] == 254
            : bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
    }

    private static IPAddress Mask(IPAddress ip, int prefixLength)
    {
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
        }

        return new IPAddress(bytes);
    }

    private static CelValue ContainsIp(CidrValue cidr, IPAddress ip)
    {
        if (cidr.Address.AddressFamily != ip.AddressFamily)
        {
            return BoolValue.False;
        }

        return BoolValue.Of(Mask(ip, cidr.PrefixLength).Equals(Mask(cidr.Address, cidr.PrefixLength)));
    }

    private static CelValue ContainsCidr(CidrValue outer, CidrValue inner)
    {
        if (outer.Address.AddressFamily != inner.Address.AddressFamily || inner.PrefixLength < outer.PrefixLength)
        {
            return BoolValue.False;
        }

        return BoolValue.Of(Mask(inner.Address, outer.PrefixLength).Equals(Mask(outer.Address, outer.PrefixLength)));
    }
}
