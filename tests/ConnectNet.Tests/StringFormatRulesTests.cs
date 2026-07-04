using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class StringFormatRulesTests
{
    private readonly ProtoValidator _validator = new();

    private void AssertFormatValid(StringFormatTestMessage msg, string field)
    {
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == field);
    }

    private void AssertFormatInvalid(StringFormatTestMessage msg, string field, string constraintId)
    {
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == field && v.ConstraintId == constraintId);
    }

    // --- string.len / min_len / max_len count code points, not UTF-16 units ---

    [Fact]
    public void Len_CountsCodePoints_SurrogatePairs()
    {
        // 3 emoji = 3 code points but 6 UTF-16 units
        var msg = new StringLenTestMessage { LenVal = "\U0001F600\U0001F600\U0001F600" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "lenVal");
    }

    [Fact]
    public void Len_WrongCodePointCount_Violation()
    {
        var msg = new StringLenTestMessage { LenVal = "\U0001F600\U0001F600" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "lenVal" && v.ConstraintId == "string.len");
    }

    [Fact]
    public void MaxLen_CountsCodePoints()
    {
        var msg = new StringLenTestMessage { MaxLenVal = "\U0001F600\U0001F600\U0001F600" }; // 3 code points <= 3
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "maxLenVal");
    }

    [Fact]
    public void MaxLen_TooManyCodePoints_Violation()
    {
        var msg = new StringLenTestMessage { MaxLenVal = "abcd" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "maxLenVal" && v.ConstraintId == "string.max_len");
    }

    // --- len_bytes / min_bytes / max_bytes ---

    [Fact]
    public void LenBytes_Utf8ByteCount()
    {
        var msg = new StringLenTestMessage { LenBytesVal = "あ" }; // "あ" = 3 UTF-8 bytes
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "lenBytesVal");
    }

    [Fact]
    public void LenBytes_WrongByteCount_Violation()
    {
        var msg = new StringLenTestMessage { LenBytesVal = "ab" }; // 2 bytes != 3
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "lenBytesVal" && v.ConstraintId == "string.len_bytes");
    }

    [Fact]
    public void MinBytes_TooFew_Violation()
    {
        var msg = new StringLenTestMessage { MinBytesVal = "ab" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "minBytesVal" && v.ConstraintId == "string.min_bytes");
    }

    [Fact]
    public void MaxBytes_TooMany_Violation()
    {
        var msg = new StringLenTestMessage { MaxBytesVal = "あいう" }; // 9 bytes > 6
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "maxBytesVal" && v.ConstraintId == "string.max_bytes");
    }

    [Fact]
    public void MaxBytes_WithinLimit_NoViolation()
    {
        var msg = new StringLenTestMessage { MaxBytesVal = "あい" }; // 6 bytes <= 6
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "maxBytesVal");
    }

    // --- email ---

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("a@b")] // single-label domain is allowed by protovalidate
    [InlineData("first.last@sub.example.com")]
    public void Email_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Email = value }, "email");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b@c")]
    [InlineData("a..b@example.com")]     // consecutive dots in local part
    [InlineData(".a@example.com")]       // leading dot
    [InlineData("a.@example.com")]       // trailing dot
    [InlineData("a@-example.com")]       // label starts with hyphen
    [InlineData("a@example-.com")]       // label ends with hyphen
    [InlineData("a@exa mple.com")]
    public void Email_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Email = value }, "email", "string.email");
    }

    [Fact]
    public void Email_LocalPartTooLong_Violation()
    {
        var local = new string('a', 65); // > 64
        AssertFormatInvalid(new StringFormatTestMessage { Email = local + "@example.com" }, "email", "string.email");
    }

    [Fact]
    public void Email_TotalTooLong_Violation()
    {
        var value = "a@" + string.Join(".", System.Linq.Enumerable.Repeat("abcdefgh", 32)); // way over 254
        AssertFormatInvalid(new StringFormatTestMessage { Email = value }, "email", "string.email");
    }

    // --- hostname ---

    [Theory]
    [InlineData("example.com")]
    [InlineData("example.com.")]  // trailing dot allowed
    [InlineData("a-b.example")]
    [InlineData("localhost")]
    public void Hostname_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Hostname = value }, "hostname");
    }

    [Theory]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("example..com")]
    [InlineData("example.123")]  // last label all digits
    [InlineData("exa_mple.com")]
    public void Hostname_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Hostname = value }, "hostname", "string.hostname");
    }

    [Fact]
    public void Hostname_LabelTooLong_Violation()
    {
        var value = new string('a', 64) + ".com"; // label > 63
        AssertFormatInvalid(new StringFormatTestMessage { Hostname = value }, "hostname", "string.hostname");
    }

    // --- ipv4 ---

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void Ipv4_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Ipv4 = value }, "ipv4");
    }

    [Theory]
    [InlineData("1")]            // .NET parses as 0.0.0.1
    [InlineData("127.1")]        // .NET shorthand
    [InlineData("010.1.1.1")]    // leading zero
    [InlineData("256.0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("::1")]
    public void Ipv4_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Ipv4 = value }, "ipv4", "string.ipv4");
    }

    // --- ipv6 ---

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    [InlineData("::ffff:192.0.2.1")]
    public void Ipv6_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Ipv6 = value }, "ipv6");
    }

    [Theory]
    [InlineData("[::1]")]        // brackets
    [InlineData("::1%eth0")]     // zone id
    [InlineData("fe80::1%25")]
    [InlineData("127.0.0.1")]
    [InlineData("12345::")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    public void Ipv6_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Ipv6 = value }, "ipv6", "string.ipv6");
    }

    // --- ip ---

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Ip_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Ip = value }, "ip");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("127.1")]
    [InlineData("[::1]")]
    [InlineData("::1%eth0")]
    public void Ip_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Ip = value }, "ip", "string.ip");
    }

    // --- uri ---

    [Theory]
    [InlineData("https://example.com/path?q=1#frag")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("urn:isbn:0451450523")]
    [InlineData("ftp://ftp.example.com/file")]
    public void Uri_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Uri = value }, "uri");
    }

    [Theory]
    [InlineData("/foo")]              // no scheme; must not be treated as file URI
    [InlineData("example.com")]
    [InlineData("https://exa mple.com/")]
    [InlineData("1http://example.com")]
    public void Uri_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Uri = value }, "uri", "string.uri");
    }

    // --- uri_ref ---

    [Theory]
    [InlineData("")]                  // same-document reference
    [InlineData("/foo/bar?q=1")]
    [InlineData("//example.com/x")]
    [InlineData("relative/path")]
    [InlineData("#frag")]
    [InlineData("https://example.com/")]
    public void UriRef_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { UriRef = value }, "uriRef");
    }

    [Theory]
    [InlineData("/foo/%zzbar")]       // invalid percent-encoding
    [InlineData("%")]
    [InlineData("/a b")]              // space
    public void UriRef_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { UriRef = value }, "uriRef", "string.uri_ref");
    }

    // --- uuid / tuuid ---

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("550E8400-E29B-41D4-A716-446655440000")]
    public void Uuid_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Uuid = value }, "uuid");
    }

    [Theory]
    [InlineData("550e8400e29b41d4a716446655440000")]      // missing dashes
    [InlineData("550e8400-e29b-41d4-a716-44665544000")]   // too short
    [InlineData("g50e8400-e29b-41d4-a716-446655440000")]  // bad hex
    public void Uuid_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Uuid = value }, "uuid", "string.uuid");
    }

    [Theory]
    [InlineData("550e8400e29b41d4a716446655440000")]
    public void Tuuid_Valid(string value)
    {
        AssertFormatValid(new StringFormatTestMessage { Tuuid = value }, "tuuid");
    }

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]  // dashes not allowed
    [InlineData("550e8400e29b41d4a71644665544000")]       // 31 chars
    public void Tuuid_Invalid(string value)
    {
        AssertFormatInvalid(new StringFormatTestMessage { Tuuid = value }, "tuuid", "string.tuuid");
    }
}
