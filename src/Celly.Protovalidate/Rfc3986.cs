namespace Celly.Protovalidate;

/// <summary>
/// RFC 3986 URI / URI-reference validation, matching protovalidate's <c>isUri</c>/<c>isUriRef</c>.
/// A strict recursive-descent recognizer over the ABNF — .NET's <see cref="System.Uri"/> is far too
/// lenient (accepts control characters, trailing spaces) to match the conformance suite.
/// </summary>
internal static class Rfc3986
{
    /// <summary>A full URI: scheme ":" hier-part [ "?" query ] [ "#" fragment ].</summary>
    public static bool IsUri(string s)
    {
        var p = new Parser(s);
        return p.Uri() && p.AtEnd;
    }

    /// <summary>A URI-reference: a URI or a relative-ref.</summary>
    public static bool IsUriRef(string s)
    {
        var uri = new Parser(s);
        if (uri.Uri() && uri.AtEnd)
        {
            return true;
        }

        var rel = new Parser(s);
        return rel.RelativeRef() && rel.AtEnd;
    }

    private struct Parser(string s)
    {
        private readonly string _s = s;
        private int _i;

        public readonly bool AtEnd => _i == _s.Length;

        private readonly char Cur => _i < _s.Length ? _s[_i] : '\0';

        private bool Take(char c)
        {
            if (Cur == c)
            {
                _i++;
                return true;
            }

            return false;
        }

        public bool Uri()
        {
            if (!Scheme() || !Take(':') || !HierPart())
            {
                return false;
            }

            return QueryAndFragment();
        }

        public bool RelativeRef()
        {
            if (!RelativePart())
            {
                return false;
            }

            return QueryAndFragment();
        }

        private bool QueryAndFragment()
        {
            if (Take('?') && !Query())
            {
                return false;
            }

            if (Take('#') && !Fragment())
            {
                return false;
            }

            return true;
        }

        // scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
        private bool Scheme()
        {
            if (!char.IsAsciiLetter(Cur))
            {
                return false;
            }

            _i++;
            while (char.IsAsciiLetterOrDigit(Cur) || Cur is '+' or '-' or '.')
            {
                _i++;
            }

            return true;
        }

        // hier-part = "//" authority path-abempty / path-absolute / path-rootless / path-empty
        private bool HierPart()
        {
            if (Cur == '/' && Peek(1) == '/')
            {
                _i += 2;
                return Authority() && PathAbEmpty();
            }

            return PathAbsolute() || PathRootless() || true; // path-empty always matches
        }

        // relative-part = "//" authority path-abempty / path-absolute / path-noscheme / path-empty
        private bool RelativePart()
        {
            if (Cur == '/' && Peek(1) == '/')
            {
                _i += 2;
                return Authority() && PathAbEmpty();
            }

            return PathAbsolute() || PathNoScheme() || true;
        }

        private readonly char Peek(int n) => _i + n < _s.Length ? _s[_i + n] : '\0';

        // authority = [ userinfo "@" ] host [ ":" port ]
        private bool Authority()
        {
            var save = _i;
            if (UserInfo() && Take('@'))
            {
                // userinfo consumed
            }
            else
            {
                _i = save;
            }

            if (!Host())
            {
                return false;
            }

            if (Take(':'))
            {
                while (char.IsAsciiDigit(Cur))
                {
                    _i++;
                }
            }

            return true;
        }

        // userinfo = *( unreserved / pct-encoded / sub-delims / ":" )
        private bool UserInfo()
        {
            while (true)
            {
                if (Unreserved(Cur) || SubDelim(Cur) || Cur == ':')
                {
                    _i++;
                }
                else if (!Pct())
                {
                    break;
                }
            }

            return true;
        }

        // host = IP-literal / IPv4address / reg-name
        private bool Host()
        {
            if (Cur == '[')
            {
                return IpLiteral();
            }

            // reg-name = *( unreserved / pct-encoded / sub-delims ); IPv4 is a subset.
            var start = _i;
            while (true)
            {
                if (Unreserved(Cur) || SubDelim(Cur))
                {
                    _i++;
                }
                else if (!Pct())
                {
                    break;
                }
            }

            // Percent-encoded octets must decode to valid UTF-8.
            return IsValidPctUtf8(_s[start.._i]);
        }

        // IP-literal = "[" ( IPv6address / IPvFuture ) "]"
        private bool IpLiteral()
        {
            if (!Take('['))
            {
                return false;
            }

            if (Cur == 'v' || Cur == 'V')
            {
                _i++;
                var hex = 0;
                while (char.IsAsciiHexDigit(Cur)) { _i++; hex++; }
                if (hex == 0 || !Take('.'))
                {
                    return false;
                }

                var n = 0;
                while (Unreserved(Cur) || SubDelim(Cur) || Cur == ':') { _i++; n++; }
                if (n == 0)
                {
                    return false;
                }
            }
            else
            {
                // IPv6address [ "%25" ZoneID ]: collect to ']' and validate strictly.
                var start = _i;
                while (_i < _s.Length && Cur != ']') { _i++; }
                if (_i >= _s.Length || !IsIpv6Literal(_s[start.._i]))
                {
                    return false;
                }
            }

            return Take(']');
        }

        private static bool IsIpv6Literal(string content)
        {
            var zoneIdx = content.IndexOf("%25", StringComparison.Ordinal);
            var addr = zoneIdx < 0 ? content : content[..zoneIdx];
            // A zone must be introduced by "%25"; a bare '%' in the address is invalid.
            if (addr.Contains('%') || (zoneIdx >= 0 && !IsZoneId(content[(zoneIdx + 3)..])))
            {
                return false;
            }

            return System.Net.IPAddress.TryParse(addr, out var ip)
                && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        // ZoneID = 1*( unreserved / pct-encoded ), decoding to valid UTF-8.
        private static bool IsZoneId(string zone)
        {
            if (zone.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < zone.Length; i++)
            {
                if (Unreserved(zone[i]))
                {
                    continue;
                }

                if (zone[i] == '%' && i + 2 < zone.Length
                    && char.IsAsciiHexDigit(zone[i + 1]) && char.IsAsciiHexDigit(zone[i + 2]))
                {
                    i += 2;
                    continue;
                }

                return false;
            }

            return IsValidPctUtf8(zone);
        }

        // Decode percent-encoded octets (leaving literal ASCII as-is) and verify the bytes are UTF-8.
        private static bool IsValidPctUtf8(string s)
        {
            var bytes = new byte[s.Length];
            var n = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length)
                {
                    bytes[n++] = (byte)((HexVal(s[i + 1]) << 4) | HexVal(s[i + 2]));
                    i += 2;
                }
                else
                {
                    bytes[n++] = (byte)s[i];
                }
            }

            return System.Text.Unicode.Utf8.IsValid(bytes.AsSpan(0, n));
        }

        private static int HexVal(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

        private bool PathAbEmpty()
        {
            while (Cur == '/')
            {
                _i++;
                Segment();
            }

            return true;
        }

        // path-absolute = "/" [ segment-nz *( "/" segment ) ]
        private bool PathAbsolute()
        {
            if (Cur != '/')
            {
                return false;
            }

            _i++;
            if (SegmentNz())
            {
                while (Cur == '/')
                {
                    _i++;
                    Segment();
                }
            }

            return true;
        }

        // path-rootless = segment-nz *( "/" segment )
        private bool PathRootless()
        {
            if (!SegmentNz())
            {
                return false;
            }

            while (Cur == '/')
            {
                _i++;
                Segment();
            }

            return true;
        }

        // path-noscheme = segment-nz-nc *( "/" segment )
        private bool PathNoScheme()
        {
            if (!SegmentNzNc())
            {
                return false;
            }

            while (Cur == '/')
            {
                _i++;
                Segment();
            }

            return true;
        }

        private bool Segment()
        {
            while (PChar()) { }
            return true;
        }

        private bool SegmentNz()
        {
            if (!PChar())
            {
                return false;
            }

            while (PChar()) { }
            return true;
        }

        // segment-nz-nc = 1*( unreserved / pct-encoded / sub-delims / "@" )  (no colon)
        private bool SegmentNzNc()
        {
            var n = 0;
            while (true)
            {
                if (Unreserved(Cur) || SubDelim(Cur) || Cur == '@')
                {
                    _i++;
                    n++;
                }
                else if (Pct())
                {
                    n++;
                }
                else
                {
                    break;
                }
            }

            return n > 0;
        }

        // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
        private bool PChar()
        {
            if (Unreserved(Cur) || SubDelim(Cur) || Cur is ':' or '@')
            {
                _i++;
                return true;
            }

            return Pct();
        }

        // query / fragment = *( pchar / "/" / "?" )
        private bool Query() => QueryOrFragment();

        private bool Fragment() => QueryOrFragment();

        private bool QueryOrFragment()
        {
            while (PChar() || Cur is '/' or '?')
            {
                if (Cur is '/' or '?')
                {
                    _i++;
                }
            }

            return true;
        }

        // pct-encoded = "%" HEXDIG HEXDIG
        private bool Pct()
        {
            if (Cur == '%' && char.IsAsciiHexDigit(Peek(1)) && char.IsAsciiHexDigit(Peek(2)))
            {
                _i += 3;
                return true;
            }

            return false;
        }

        private static bool Unreserved(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';

        private static bool SubDelim(char c) =>
            c is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';
    }
}
