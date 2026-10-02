using System;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectNet.Validation.Cel.Protovalidate;

/// <summary>
/// The string format checks behind Protovalidate's CEL functions, ported from the reference
/// implementation's grammar-driven parsers so that every conformance case agrees. The parsers
/// run over the UTF-8 bytes of the input, as the reference does.
/// </summary>
internal static class StringRules
{
    private static readonly Regex EmailRegex = new(
        "^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(100));

    public static bool IsEmail(string value)
    {
        // The reference regex is ASCII-only; a non-ASCII byte cannot match any class, so a
        // code point check up front keeps the .NET regex honest about UTF-16 pairs.
        foreach (var c in value)
        {
            if (c > 0x7f) return false;
        }
        return EmailRegex.IsMatch(value);
    }

    public static bool IsUri(string value) => new UriParser(Encoding.UTF8.GetBytes(value)).Uri();

    public static bool IsUriRef(string value) => new UriParser(Encoding.UTF8.GetBytes(value)).UriReference();

    public static bool IsIp(string value, long version)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return IsIp(bytes, version);
    }

    private static bool IsIp(byte[] bytes, long version)
    {
        if (version == 6) return new Ipv6Parser(bytes).Address();
        if (version == 4) return new Ipv4Parser(bytes).Address();
        if (version == 0) return new Ipv4Parser(bytes).Address() || new Ipv6Parser(bytes).Address();
        return false;
    }

    public static bool IsIpPrefix(string value, long version, bool strict)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return IsIpPrefix(bytes, version, strict);
    }

    private static bool IsIpPrefix(byte[] bytes, long version, bool strict)
    {
        if (version == 6)
        {
            var ip = new Ipv6Parser(bytes);
            return ip.AddressPrefix() && (!strict || ip.IsPrefixOnly());
        }
        if (version == 4)
        {
            var ip = new Ipv4Parser(bytes);
            return ip.AddressPrefix() && (!strict || ip.IsPrefixOnly());
        }
        if (version == 0)
            return IsIpPrefix(bytes, 6, strict) || IsIpPrefix(bytes, 4, strict);
        return false;
    }

    public static bool IsHostname(string value)
    {
        var str = value.EndsWith(".", StringComparison.Ordinal) ? value.Substring(0, value.Length - 1) : value;
        var bytes = Encoding.UTF8.GetBytes(str);
        if (bytes.Length > 253)
            return false;
        bool allDigits = false;
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'.')
                continue;
            int len = i - start;
            allDigits = true;
            if (len == 0 || len > 63 || bytes[start] == (byte)'-' || bytes[i - 1] == (byte)'-')
                return false;
            for (int j = start; j < i; j++)
            {
                byte c = bytes[j];
                bool alpha = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                if (!alpha && !digit && c != '-')
                    return false;
                allDigits = allDigits && digit;
            }
            start = i + 1;
        }
        return !allDigits;
    }

    public static bool IsHostAndPort(string value, bool portRequired)
    {
        if (value.Length == 0)
            return false;
        var bytes = Encoding.UTF8.GetBytes(value);
        int splitIdx = Array.LastIndexOf(bytes, (byte)':');
        if (bytes[0] == (byte)'[')
        {
            int end = Array.LastIndexOf(bytes, (byte)']');
            if (end + 1 == bytes.Length)
                return !portRequired && IsIp(Slice(bytes, 1, end), 6);
            if (end + 1 == splitIdx)
                return IsIp(Slice(bytes, 1, end), 6) && IsPort(Slice(bytes, splitIdx + 1, bytes.Length));
            return false;
        }
        if (splitIdx < 0)
            return !portRequired && (IsHostname(value) || IsIp(bytes, 4));
        var host = Slice(bytes, 0, splitIdx);
        var port = Slice(bytes, splitIdx + 1, bytes.Length);
        return (IsHostname(Encoding.UTF8.GetString(host)) || IsIp(host, 4)) && IsPort(port);
    }

    private static byte[] Slice(byte[] bytes, int start, int end)
    {
        if (end < start) return Array.Empty<byte>();
        var result = new byte[end - start];
        Array.Copy(bytes, start, result, 0, result.Length);
        return result;
    }

    private static bool IsPort(byte[] str)
    {
        if (str.Length == 0)
            return false;
        foreach (var c in str)
        {
            if (c < '0' || c > '9') return false;
        }
        if (str.Length > 1 && str[0] == (byte)'0')
            return false;
        ulong value = 0;
        foreach (var c in str)
        {
            value = value * 10 + (ulong)(c - '0');
            if (value > uint.MaxValue) return false;
        }
        return value <= 65535;
    }

    // ---- IPv4 ----

    private sealed class Ipv4Parser
    {
        private readonly byte[] _s;
        private int _i;
        private readonly byte[] _octets = new byte[4];
        private int _octetCount;
        private long _prefixLen;

        public Ipv4Parser(byte[] s)
        {
            _s = s;
        }

        public uint Bits => _octetCount != 4 ? 0u
            : ((uint)_octets[0] << 24) | ((uint)_octets[1] << 16) | ((uint)_octets[2] << 8) | _octets[3];

        public bool IsPrefixOnly()
        {
            uint bits = Bits;
            uint mask = _prefixLen == 32 ? 0xffffffff : ~(0xffffffffu >> (int)_prefixLen);
            return bits == (bits & mask);
        }

        public bool Address() => AddressPart() && _i == _s.Length;

        public bool AddressPrefix() => AddressPart() && Take((byte)'/') && PrefixLength() && _i == _s.Length;

        private bool PrefixLength()
        {
            int start = _i;
            while (Digit())
            {
                if (_i - start > 2) return false;
            }
            int len = _i - start;
            if (len == 0) return false;
            if (len > 1 && _s[start] == (byte)'0') return false;
            long value = 0;
            for (int j = start; j < _i; j++) value = value * 10 + (_s[j] - '0');
            if (value > 32) return false;
            _prefixLen = value;
            return true;
        }

        private bool AddressPart()
        {
            int start = _i;
            if (DecOctet() && Take((byte)'.') && DecOctet() && Take((byte)'.') && DecOctet() && Take((byte)'.') && DecOctet())
                return true;
            _i = start;
            return false;
        }

        private bool DecOctet()
        {
            int start = _i;
            while (Digit())
            {
                if (_i - start > 3) return false;
            }
            int len = _i - start;
            if (len == 0) return false;
            if (len > 1 && _s[start] == (byte)'0') return false;
            int value = 0;
            for (int j = start; j < _i; j++) value = value * 10 + (_s[j] - '0');
            if (value > 255) return false;
            if (_octetCount < 4) _octets[_octetCount] = (byte)value;
            _octetCount++;
            return true;
        }

        private bool Digit()
        {
            if (_i >= _s.Length) return false;
            if (_s[_i] >= '0' && _s[_i] <= '9') { _i++; return true; }
            return false;
        }

        private bool Take(byte c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }
    }

    // ---- IPv6 ----

    private sealed class Ipv6Parser
    {
        private readonly byte[] _s;
        private int _i;
        private readonly System.Collections.Generic.List<ushort> _pieces = new();
        private int _doubleColonAt = -1;
        private bool _doubleColonSeen;
        private byte[] _dottedRaw = Array.Empty<byte>();
        private Ipv4Parser? _dottedAddr;
        private bool _zoneIdFound;
        private long _prefixLen;

        public Ipv6Parser(byte[] s)
        {
            _s = s;
        }

        private ulong[] Bits()
        {
            var p16 = new System.Collections.Generic.List<ushort>(_pieces);
            if (_dottedAddr != null)
            {
                uint dotted = _dottedAddr.Bits;
                p16.Add((ushort)(dotted >> 16));
                p16.Add((ushort)dotted);
            }
            if (_doubleColonSeen)
            {
                while (p16.Count < 8)
                    p16.Insert(_doubleColonAt, 0);
            }
            if (p16.Count != 8)
                return new ulong[] { 0, 0 };
            return new[]
            {
                ((ulong)p16[0] << 48) | ((ulong)p16[1] << 32) | ((ulong)p16[2] << 16) | p16[3],
                ((ulong)p16[4] << 48) | ((ulong)p16[5] << 32) | ((ulong)p16[6] << 16) | p16[7],
            };
        }

        public bool IsPrefixOnly()
        {
            var bits = Bits();
            for (int idx = 0; idx < 2; idx++)
            {
                long size = _prefixLen - 64 * idx;
                ulong mask = size >= 64 ? 0xFFFFFFFFFFFFFFFF : size < 0 ? 0 : ~(0xFFFFFFFFFFFFFFFF >> (int)size);
                if (bits[idx] != (bits[idx] & mask))
                    return false;
            }
            return true;
        }

        public bool Address() => AddressPart() && _i == _s.Length;

        public bool AddressPrefix() =>
            AddressPart() && !_zoneIdFound && Take((byte)'/') && PrefixLength() && _i == _s.Length;

        private bool PrefixLength()
        {
            int start = _i;
            while (Digit())
            {
                if (_i - start > 3) return false;
            }
            int len = _i - start;
            if (len == 0) return false;
            if (len > 1 && _s[start] == (byte)'0') return false;
            long value = 0;
            for (int j = start; j < _i; j++) value = value * 10 + (_s[j] - '0');
            if (value > 128) return false;
            _prefixLen = value;
            return true;
        }

        private bool AddressPart()
        {
            while (_i < _s.Length)
            {
                if ((_doubleColonSeen || _pieces.Count == 6) && Dotted())
                {
                    var dotted = new Ipv4Parser(_dottedRaw);
                    if (dotted.Address())
                    {
                        _dottedAddr = dotted;
                        return true;
                    }
                    return false;
                }
                var (ok, err) = H16();
                if (err) return false;
                if (ok) continue;
                if (Take((byte)':'))
                {
                    if (Take((byte)':'))
                    {
                        if (_doubleColonSeen) return false;
                        _doubleColonSeen = true;
                        _doubleColonAt = _pieces.Count;
                        if (Take((byte)':')) return false;
                    }
                    else if (_i == 1 || _i == _s.Length)
                    {
                        return false;
                    }
                    continue;
                }
                if (_s[_i] == (byte)'%' && !ZoneId())
                    return false;
                break;
            }
            if (_doubleColonSeen)
                return _pieces.Count < 8;
            return _pieces.Count == 8;
        }

        private bool ZoneId()
        {
            int start = _i;
            if (Take((byte)'%'))
            {
                if (_s.Length - _i > 0)
                {
                    _i = _s.Length;
                    _zoneIdFound = true;
                    return true;
                }
            }
            _i = start;
            _zoneIdFound = false;
            return false;
        }

        private bool Dotted()
        {
            int start = _i;
            _dottedRaw = Array.Empty<byte>();
            while (Digit() || Take((byte)'.'))
            {
            }
            if (_i - start >= 7)
            {
                _dottedRaw = new byte[_i - start];
                Array.Copy(_s, start, _dottedRaw, 0, _dottedRaw.Length);
                return true;
            }
            _i = start;
            return false;
        }

        private (bool Ok, bool Error) H16()
        {
            int start = _i;
            while (HexDigit())
            {
                if (_i - start > 4) return (false, true);
            }
            int len = _i - start;
            if (len == 0) return (false, false);
            int value = 0;
            for (int j = start; j < _i; j++) value = value * 16 + HexValue(_s[j]);
            _pieces.Add((ushort)value);
            return (true, false);
        }

        private static int HexValue(byte c) =>
            c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c - 'A' + 10;

        private bool HexDigit()
        {
            if (_i >= _s.Length) return false;
            byte c = _s[_i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) { _i++; return true; }
            return false;
        }

        private bool Digit()
        {
            if (_i >= _s.Length) return false;
            if (_s[_i] >= '0' && _s[_i] <= '9') { _i++; return true; }
            return false;
        }

        private bool Take(byte c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }
    }

    // ---- URI (RFC 3986) ----

    private sealed class UriParser
    {
        private readonly byte[] _s;
        private int _i;
        private bool _pctEncodedFound;

        public UriParser(byte[] s)
        {
            _s = s;
        }

        public bool Uri()
        {
            int start = _i;
            if (!(Scheme() && Take((byte)':') && HierPart()))
            {
                _i = start;
                return false;
            }
            if (Take((byte)'?') && !Query())
                return false;
            if (Take((byte)'#') && !Fragment())
                return false;
            if (_i != _s.Length)
            {
                _i = start;
                return false;
            }
            return true;
        }

        public bool UriReference() => Uri() || RelativeRef();

        private bool HierPart()
        {
            int start = _i;
            if (TakeDoubleSlash() && Authority() && PathAbempty())
                return true;
            _i = start;
            return PathAbsolute() || PathRootless() || PathEmpty();
        }

        private bool RelativeRef()
        {
            int start = _i;
            if (!RelativePart())
                return false;
            if (Take((byte)'?') && !Query())
            {
                _i = start;
                return false;
            }
            if (Take((byte)'#') && !Fragment())
            {
                _i = start;
                return false;
            }
            if (_i != _s.Length)
            {
                _i = start;
                return false;
            }
            return true;
        }

        private bool RelativePart()
        {
            int start = _i;
            if (TakeDoubleSlash() && Authority() && PathAbempty())
                return true;
            _i = start;
            return PathAbsolute() || PathNoscheme() || PathEmpty();
        }

        private bool Scheme()
        {
            int start = _i;
            if (Alpha())
            {
                while (Alpha() || Digit() || Take((byte)'+') || Take((byte)'-') || Take((byte)'.'))
                {
                }
                if (Peek((byte)':'))
                    return true;
            }
            _i = start;
            return false;
        }

        private bool Authority()
        {
            int start = _i;
            if (Userinfo())
            {
                if (!Take((byte)'@'))
                {
                    _i = start;
                    return false;
                }
            }
            if (!Host())
            {
                _i = start;
                return false;
            }
            if (Take((byte)':'))
            {
                if (!Port())
                {
                    _i = start;
                    return false;
                }
            }
            if (!IsAuthorityEnd())
            {
                _i = start;
                return false;
            }
            return true;
        }

        private bool IsAuthorityEnd() =>
            _i >= _s.Length || _s[_i] == (byte)'?' || _s[_i] == (byte)'#' || _s[_i] == (byte)'/';

        private bool Userinfo()
        {
            int start = _i;
            while (true)
            {
                if (Unreserved() || PctEncoded() || SubDelims() || Take((byte)':'))
                    continue;
                if (_i < _s.Length && _s[_i] == (byte)'@')
                    return true;
                _i = start;
                return false;
            }
        }

        private static bool CheckHostPctEncoded(byte[] s, int start, int end)
        {
            var escaped = new System.Collections.Generic.List<byte>(end - start);
            for (int i = start; i < end;)
            {
                if (s[i] == (byte)'%')
                {
                    escaped.Add((byte)((Unhex(s[i + 1]) << 4) | Unhex(s[i + 2])));
                    i += 3;
                }
                else
                {
                    escaped.Add(s[i]);
                    i++;
                }
            }
            return Runtime.Utf8.IsValid(escaped.ToArray());
        }

        private static int Unhex(byte c) =>
            c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;

        private bool Host()
        {
            int start = _i;
            _pctEncodedFound = false;
            if ((Peek((byte)'[') && IpLiteral()) || RegName())
            {
                if (_pctEncodedFound && !CheckHostPctEncoded(_s, start, _i))
                    return false;
                return true;
            }
            return false;
        }

        private bool Port()
        {
            int start = _i;
            while (Digit())
            {
            }
            if (IsAuthorityEnd())
                return true;
            _i = start;
            return false;
        }

        private bool IpLiteral()
        {
            int start = _i;
            if (Take((byte)'['))
            {
                int cur = _i;
                if (Ipv6Address() && Take((byte)']')) return true;
                _i = cur;
                if (Ipv6Addrz() && Take((byte)']')) return true;
                _i = cur;
                if (IpvFuture() && Take((byte)']')) return true;
            }
            _i = start;
            return false;
        }

        private bool Ipv6Address()
        {
            int start = _i;
            while (HexDigit() || Take((byte)':'))
            {
            }
            var slice = new byte[_i - start];
            Array.Copy(_s, start, slice, 0, slice.Length);
            if (StringRules.IsIp(slice, 6))
                return true;
            _i = start;
            return false;
        }

        private bool Ipv6Addrz()
        {
            int start = _i;
            if (Ipv6Address() && Take((byte)'%') && Take((byte)'2') && Take((byte)'5') && ZoneId())
                return true;
            _i = start;
            return false;
        }

        private bool ZoneId()
        {
            int start = _i;
            while (Unreserved() || PctEncoded())
            {
            }
            if (_i - start > 0)
                return true;
            _i = start;
            return false;
        }

        private bool IpvFuture()
        {
            int start = _i;
            if (Take((byte)'v') && HexDigit())
            {
                while (HexDigit())
                {
                }
                if (Take((byte)'.'))
                {
                    int counter = 0;
                    while (Unreserved() || SubDelims() || Take((byte)':'))
                        counter++;
                    if (counter >= 1)
                        return true;
                }
            }
            _i = start;
            return false;
        }

        private bool RegName()
        {
            int start = _i;
            while (true)
            {
                if (Unreserved() || PctEncoded() || SubDelims())
                    continue;
                if (IsAuthorityEnd())
                    return true;
                if (_s[_i] == (byte)':')
                    return true;
                _i = start;
                return false;
            }
        }

        private bool IsPathEnd() => _i >= _s.Length || _s[_i] == (byte)'?' || _s[_i] == (byte)'#';

        private bool PathAbempty()
        {
            int start = _i;
            while (Take((byte)'/') && Segment())
            {
            }
            if (IsPathEnd())
                return true;
            _i = start;
            return false;
        }

        private bool PathAbsolute()
        {
            int start = _i;
            if (Take((byte)'/'))
            {
                if (SegmentNz())
                {
                    while (Take((byte)'/') && Segment())
                    {
                    }
                }
                if (IsPathEnd())
                    return true;
            }
            _i = start;
            return false;
        }

        private bool PathNoscheme()
        {
            int start = _i;
            if (SegmentNzNc())
            {
                while (Take((byte)'/') && Segment())
                {
                }
                if (IsPathEnd())
                    return true;
            }
            _i = start;
            return false;
        }

        private bool PathRootless()
        {
            int start = _i;
            if (SegmentNz())
            {
                while (Take((byte)'/') && Segment())
                {
                }
                if (IsPathEnd())
                    return true;
            }
            _i = start;
            return false;
        }

        private bool PathEmpty() => IsPathEnd();

        private bool Segment()
        {
            while (Pchar())
            {
            }
            return true;
        }

        private bool SegmentNz()
        {
            int start = _i;
            if (Pchar())
                return Segment();
            _i = start;
            return false;
        }

        private bool SegmentNzNc()
        {
            int start = _i;
            while (Unreserved() || PctEncoded() || SubDelims() || Take((byte)'@'))
            {
            }
            if (_i - start > 0)
                return true;
            _i = start;
            return false;
        }

        private bool Pchar() => Unreserved() || PctEncoded() || SubDelims() || Take((byte)':') || Take((byte)'@');

        private bool Query()
        {
            int start = _i;
            while (true)
            {
                if (Pchar() || Take((byte)'/') || Take((byte)'?'))
                    continue;
                if (_i == _s.Length || _s[_i] == (byte)'#')
                    return true;
                _i = start;
                return false;
            }
        }

        private bool Fragment()
        {
            int start = _i;
            while (true)
            {
                if (Pchar() || Take((byte)'/') || Take((byte)'?'))
                    continue;
                if (_i == _s.Length)
                    return true;
                _i = start;
                return false;
            }
        }

        private bool PctEncoded()
        {
            int start = _i;
            if (Take((byte)'%') && HexDigit() && HexDigit())
            {
                _pctEncodedFound = true;
                return true;
            }
            _i = start;
            return false;
        }

        private bool Unreserved() =>
            Alpha() || Digit() || Take((byte)'-') || Take((byte)'_') || Take((byte)'.') || Take((byte)'~');

        private bool SubDelims() =>
            Take((byte)'!') || Take((byte)'$') || Take((byte)'&') || Take((byte)'\'') || Take((byte)'(') || Take((byte)')')
            || Take((byte)'*') || Take((byte)'+') || Take((byte)',') || Take((byte)';') || Take((byte)'=');

        private bool Alpha()
        {
            if (_i >= _s.Length) return false;
            byte c = _s[_i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { _i++; return true; }
            return false;
        }

        private bool Digit()
        {
            if (_i >= _s.Length) return false;
            if (_s[_i] >= '0' && _s[_i] <= '9') { _i++; return true; }
            return false;
        }

        private bool HexDigit()
        {
            if (_i >= _s.Length) return false;
            byte c = _s[_i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) { _i++; return true; }
            return false;
        }

        private bool Take(byte c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        private bool TakeDoubleSlash() => Take((byte)'/') && Take((byte)'/');

        private bool Peek(byte c) => _i < _s.Length && _s[_i] == c;
    }
}
