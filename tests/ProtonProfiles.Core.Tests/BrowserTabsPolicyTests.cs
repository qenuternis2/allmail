using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;

namespace ProtonProfiles.Core.Tests;

public class BrowserTabsPolicyTests
{
    [Theory]
    [InlineData("https://example.test/path?q=1#section")]
    [InlineData("http://localhost:8080/")]
    [InlineData("https://[::1]:8443/")]
    [InlineData("about:blank")]
    public void Mail_profiles_can_browse_web_sites_without_changing_their_home(string address)
    {
        var policy = new NavigationPolicy().ForProfile(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Mail" });
        Assert.Equal(NavigationPolicy.StartPage, policy.StartUri);
        Assert.Equal(TopLevelDecision.Allow, policy.EvaluateTopLevel(address));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/test.html")]
    [InlineData("data:text/html,test")]
    [InlineData("ms-settings:privacy")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test/\n")]
    public void Mail_tabs_reject_external_protocols_and_credentials(string address)
    {
        var policy = new NavigationPolicy().ForProfile(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Mail" });
        Assert.Equal(TopLevelDecision.Block, policy.EvaluateTopLevel(address));
        Assert.False(BrowserAddress.TryNormalize(address, out _));
    }

    [Theory]
    [InlineData("example.test", "https://example.test/")]
    [InlineData(" example.test/path ", "https://example.test/path")]
    [InlineData("localhost:8443/path", "https://localhost:8443/path")]
    [InlineData("[::1]:8443/path", "https://[::1]:8443/path")]
    [InlineData("HTTP://EXAMPLE.TEST/path", "http://example.test/path")]
    [InlineData("https://пример.рф/", "https://пример.рф/")]
    [InlineData("about:blank", "about:blank")]
    public void Address_bar_normalizes_without_search_or_external_launch(string input, string expected)
    {
        Assert.True(BrowserAddress.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://")]
    [InlineData("mail search")]
    [InlineData("https://user@example.test/")]
    [InlineData("example.test\t")]
    public void Invalid_address_bar_input_is_rejected(string? input) => Assert.False(BrowserAddress.TryNormalize(input, out _));
}
