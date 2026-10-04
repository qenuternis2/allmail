using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class InternalPageHeadersTests
{
    [Theory]
    [InlineData("https://probe.protonprofiles.invalid", true)]
    [InlineData("https://probe.protonprofiles.invalid/fingerprint.html?build=old", true)]
    [InlineData("https://PROBE.PROTONPROFILES.INVALID/", true)]
    [InlineData("http://probe.protonprofiles.invalid:8443/", true)]
    [InlineData("https://ua-hints-bootstrap.protonprofiles.invalid/", true)]
    [InlineData("https://protonprofiles.invalid/", true)]
    [InlineData("https://diagnostics.invalid/", true)]
    [InlineData("https://diagnostics.invalid/fingerprint.html", true)]
    [InlineData("https://contexts.invalid/context.html", true)]
    [InlineData("https://mail.proton.me/", false)]
    [InlineData("https://accounts.proton.me/", false)]
    [InlineData("https://httpbin.org/", false)]
    [InlineData("https://api.ipify.org/", false)]
    [InlineData("https://probe.protonprofiles.invalid.example.com/", false)]
    [InlineData("https://diagnostics.invalid.example.com/", false)]
    [InlineData("https://example.com/path?next=https://probe.protonprofiles.invalid/", false)]
    [InlineData("https://probe.protonprofiles.invalid@example.com/", false)]
    [InlineData("https://notprotonprofiles.invalid/", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognizes_only_reserved_internal_hosts(string? value, bool expected)
        => Assert.Equal(expected, InternalPageHeaders.IsInternalUri(value));
}
