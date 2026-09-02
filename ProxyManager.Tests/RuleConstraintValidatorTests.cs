using ProxyManager.Standalone;
using Strings = ProxyManager.Standalone.Localization.Strings;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RuleConstraintValidatorTests
{
    [Theory]
    [InlineData("example.com", true)]
    [InlineData("*.example.com", true)]
    [InlineData("example.com, *.b.com; sub.c.org", true)]
    [InlineData("a.com b.com\tc.net", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    [InlineData("*example.com", false)]
    [InlineData("a..b", false)]
    [InlineData("-bad.com", false)]
    [InlineData("bad-.com", false)]
    [InlineData("localhost", false)]
    [InlineData("sub_example.com", false)]
    public void IsValidHostList_MatchesBuilderSemantics(string? raw, bool expected)
    {
        Assert.Equal(expected, RuleConstraintValidator.IsValidHostList(raw));
    }

    [Fact]
    public void IsValidHostList_RejectsHostLongerThan253()
    {
        var tooLong = string.Join(".", new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 63));
        Assert.Equal(255, tooLong.Length);
        Assert.False(RuleConstraintValidator.IsValidHostList(tooLong));
    }

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db8::/32", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("1.2.3.4, ::1; 10.0.0.0/8", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("300.1.1.1", false)]
    [InlineData("abc", false)]
    [InlineData("2001:db8::/129", false)]
    [InlineData("1.2.3.4/", false)]
    [InlineData("1.2.3.4/+8", false)]
    [InlineData("1.2.3.4/8/8", false)]
    public void IsValidIpList_MatchesBuilderSemantics(string? raw, bool expected)
    {
        Assert.Equal(expected, RuleConstraintValidator.IsValidIpList(raw));
    }

    [Theory]
    [InlineData("443", true)]
    [InlineData("1000-2000", true)]
    [InlineData("1000:2000", true)]
    [InlineData("443, 8080; 1000-2000 53", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("2000-1000", false)]
    [InlineData("abc", false)]
    [InlineData("1-2:3", false)]
    [InlineData("443-", false)]
    public void IsValidPortList_MatchesBuilderSemantics(string? raw, bool expected)
    {
        Assert.Equal(expected, RuleConstraintValidator.IsValidPortList(raw));
    }

    [Fact]
    public void Explain_ReturnsNoErrorsForValidOrEmptyInput()
    {
        Assert.Empty(RuleConstraintValidator.Explain("example.com, *.b.com", "10.0.0.0/8, ::1", "443, 1000-2000"));
        Assert.Empty(RuleConstraintValidator.Explain(null, "", "   "));
    }

    [Fact]
    public void Explain_ReturnsLocalizedMessagePerBrokenField()
    {
        var errors = RuleConstraintValidator.Explain("a..b", "", "0");

        Assert.Equal(2, errors.Count);
        Assert.Contains(Strings.RuleEditBadHosts, errors);
        Assert.Contains(Strings.RuleEditBadPorts, errors);
    }

    [Fact]
    public void Explain_ReturnsIpErrorOnlyForBrokenIps()
    {
        var errors = RuleConstraintValidator.Explain(null, "abc", null);

        var error = Assert.Single(errors);
        Assert.Equal(Strings.RuleEditBadIps, error);
    }
}
