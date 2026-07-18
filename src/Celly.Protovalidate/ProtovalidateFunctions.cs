using System.Net;
using System.Net.Sockets;
using Celly;
using Celly.Protobuf;
using Celly.Stdlib;
using Celly.Values;
using Google.Protobuf;

namespace Celly.Protovalidate;

/// <summary>
/// protovalidate's extended CEL function library — the receiver-style helpers its standard rules
/// call (<c>this.isEmail()</c>, <c>this.isIp()</c>, …) plus the global <c>getField(msg, name)</c>
/// used by predefined-rule expressions. Registered as runtime functions; the rules are evaluated
/// unchecked (dyn), so no checker declarations are needed.
/// </summary>
internal static class ProtovalidateFunctions
{
    public static CelLibrary Create(ProtoTypeRegistry registry) => new()
    {
        Name = "protovalidate",
        Functions = reg =>
        {
            // getField(message, fieldName) → the field's value (default when unset), as protovalidate
            // uses it to read sibling rule values, e.g. getField(rules, 'const').
            reg.Register("getField", args =>
            {
                if (args is [{ } target, StringValue name] && target.ToNative() is IMessage message)
                {
                    var field = message.Descriptor.FindFieldByName(name.Value);
                    return field is null ? new ErrorValue($"no such field: {name.Value}") : registry.AdaptField(message, field);
                }

                return ErrorValue.NoSuchOverload();
            });

            reg.Register("isNan", args => args is [DoubleValue d]
                ? BoolValue.Of(double.IsNaN(d.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("isInf", args => args switch
            {
                [DoubleValue d] => BoolValue.Of(double.IsInfinity(d.Value)),
                [DoubleValue d, IntValue sign] => BoolValue.Of(sign.Value switch
                {
                    > 0 => double.IsPositiveInfinity(d.Value),
                    < 0 => double.IsNegativeInfinity(d.Value),
                    _ => double.IsInfinity(d.Value),
                }),
                _ => ErrorValue.NoSuchOverload(),
            });

            reg.Register("isIp", args => args switch
            {
                [StringValue s] => BoolValue.Of(IsIp(s.Value, 0)),
                [StringValue s, IntValue v] => BoolValue.Of(IsIp(s.Value, v.Value)),
                _ => ErrorValue.NoSuchOverload(),
            });

            reg.Register("isIpPrefix", args => args switch
            {
                [StringValue s] => BoolValue.Of(IsIpPrefix(s.Value, 0, false)),
                [StringValue s, BoolValue strict] => BoolValue.Of(IsIpPrefix(s.Value, 0, strict.Value)),
                [StringValue s, IntValue v] => BoolValue.Of(IsIpPrefix(s.Value, v.Value, false)),
                [StringValue s, IntValue v, BoolValue strict] => BoolValue.Of(IsIpPrefix(s.Value, v.Value, strict.Value)),
                _ => ErrorValue.NoSuchOverload(),
            });

            reg.Register("isEmail", args => args is [StringValue s]
                ? BoolValue.Of(IsEmail(s.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("isHostname", args => args is [StringValue s]
                ? BoolValue.Of(IsHostname(s.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("isHostAndPort", args => args is [StringValue s, BoolValue portRequired]
                ? BoolValue.Of(IsHostAndPort(s.Value, portRequired.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("isUri", args => args is [StringValue s]
                ? BoolValue.Of(Rfc3986.IsUri(s.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("isUriRef", args => args is [StringValue s]
                ? BoolValue.Of(Rfc3986.IsUriRef(s.Value)) : ErrorValue.NoSuchOverload());

            reg.Register("unique", args => args is [ListValue list]
                ? BoolValue.Of(IsUnique(list)) : ErrorValue.NoSuchOverload());

            // Bytes rules call startsWith/endsWith/contains on bytes; keep the string overloads too
            // (this library loads after StringsLibrary, so we handle both here).
            reg.Register("startsWith", args => args switch
            {
                [StringValue s, StringValue p] => BoolValue.Of(s.Value.StartsWith(p.Value, StringComparison.Ordinal)),
                [BytesValue b, BytesValue p] => BoolValue.Of(b.Span.StartsWith(p.Span)),
                _ => ErrorValue.NoSuchOverload(),
            });
            reg.Register("endsWith", args => args switch
            {
                [StringValue s, StringValue p] => BoolValue.Of(s.Value.EndsWith(p.Value, StringComparison.Ordinal)),
                [BytesValue b, BytesValue p] => BoolValue.Of(b.Span.EndsWith(p.Span)),
                _ => ErrorValue.NoSuchOverload(),
            });
            reg.Register("contains", args => args switch
            {
                [StringValue s, StringValue p] => BoolValue.Of(s.Value.Contains(p.Value, StringComparison.Ordinal)),
                [BytesValue b, BytesValue p] => BoolValue.Of(b.Span.IndexOf(p.Span) >= 0),
                _ => ErrorValue.NoSuchOverload(),
            });
        },
    };

    private static bool IsIp(string s, long version) => version switch
    {
        0 => IsIpv4(s) || IsIpv6(s),
        4 => IsIpv4(s),
        6 => IsIpv6(s),
        _ => false,
    };

    // Strict dotted-quad: exactly four decimal octets 0-255 (no hex, no partial forms). .NET's
    // IPAddress.TryParse is too lenient here (accepts "127.0.1", "0x0.0.0.0").
    private static bool IsIpv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            if (int.Parse(part) > 255)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIpv6(string s)
    {
        if (s.Contains('[') || s.Contains(']'))
        {
            return false;
        }

        // An optional (non-empty) %zone-id is allowed.
        var pct = s.IndexOf('%');
        if (pct >= 0)
        {
            if (pct == s.Length - 1)
            {
                return false;
            }

            s = s[..pct];
        }

        return IPAddress.TryParse(s, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetworkV6
            && !IsIpv4(s);
    }

    private static bool IsIpPrefix(string s, long version, bool strict)
    {
        var slash = s.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var addr = s[..slash];
        var lenText = s[(slash + 1)..];
        // Strict: 1-3 digits, no leading zero (except "0"), no whitespace, no zone-id on the address.
        if (lenText.Length is 0 or > 3 || (lenText.Length > 1 && lenText[0] == '0')
            || !lenText.All(char.IsAsciiDigit) || addr.Contains('%'))
        {
            return false;
        }

        var prefixLen = int.Parse(lenText);

        var is4 = IsIpv4(addr);
        var is6 = IsIpv6(addr);
        var ok = version switch { 0 => is4 || is6, 4 => is4, 6 => is6, _ => false };
        if (!ok || !IPAddress.TryParse(addr, out var ip))
        {
            return false;
        }

        var bits = is4 ? 32 : 128;
        if (prefixLen > bits)
        {
            return false;
        }

        // strict: host bits beyond the prefix length must be zero.
        if (strict)
        {
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < bytes.Length * 8; i++)
            {
                if (i >= prefixLen && (bytes[i / 8] & (1 << (7 - i % 8))) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // RFC 5321 addr-spec: an unquoted dot-atom local part and a hostname domain (numeric labels
    // allowed, no trailing dot). Quoted local parts and non-ASCII are rejected.
    private static bool IsEmail(string s)
    {
        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || s.Length > 254)
        {
            return false;
        }

        var local = s[..at];
        var domain = s[(at + 1)..];
        return IsEmailLocal(local) && !domain.EndsWith('.') && IsHostnameLabels(domain);
    }

    private static bool IsEmailLocal(string local)
    {
        if (local.Length == 0)
        {
            return false;
        }

        foreach (var c in local)
        {
            if (c != '.' && !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~".Contains(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHostname(string s)
    {
        if (s.Length > 253)
        {
            return false;
        }

        var host = s.EndsWith('.') ? s[..^1] : s;
        // The rightmost label may not be entirely numeric (that would look like an IPv4 address).
        return IsHostnameLabels(host) && !host.Split('.')[^1].All(char.IsAsciiDigit);
    }

    private static bool IsHostnameLabels(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsHostAndPort(string s, bool portRequired)
    {
        if (s.Length == 0)
        {
            return false;
        }

        string? port;
        if (s.StartsWith('['))
        {
            // [IPv6(%zone)]:port — the address runs to the last ']'.
            var end = s.LastIndexOf(']');
            if (end < 0 || !IsIpv6WithZone(s[1..end]))
            {
                return false;
            }

            var rest = s[(end + 1)..];
            if (rest.Length == 0)
            {
                return !portRequired;
            }

            if (!rest.StartsWith(':'))
            {
                return false;
            }

            port = rest[1..];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            var host = colon < 0 ? s : s[..colon];
            if (!IsHostname(host) && !IsIpv4(host))
            {
                return false;
            }

            if (colon < 0)
            {
                return !portRequired;
            }

            port = s[(colon + 1)..];
        }

        return IsValidPort(port);
    }

    // Port: 1-5 digits, no leading zero, ≤ 65535.
    private static bool IsValidPort(string port)
    {
        if (port.Length is 0 or > 5 || (port.Length > 1 && port[0] == '0'))
        {
            return false;
        }

        return port.All(char.IsAsciiDigit) && int.Parse(port) <= 65535;
    }

    // A bracketed host is an IPv6 address with an optional (non-empty) %zone-id.
    private static bool IsIpv6WithZone(string s)
    {
        var pct = s.IndexOf('%');
        if (pct < 0)
        {
            return IsIpv6(s);
        }

        return pct < s.Length - 1 && IsIpv6(s[..pct]);
    }

    private static bool IsUnique(ListValue list)
    {
        var seen = new HashSet<string>();
        foreach (var item in list.Elements)
        {
            // Distinct by CEL string form; adequate for scalar item lists.
            if (!seen.Add(item.ToString() ?? string.Empty))
            {
                return false;
            }
        }

        return true;
    }
}
