using System.Text;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class ProfileStartPageTests
{
    [Fact]
    public void Default_mail_url_preserves_legacy_profile_and_reminders()
    {
        using var env=new TestEnv();var mail=env.AddProfile();
        Assert.Equal(NavigationPolicy.StartPage.AbsoluteUri,ProfileStartPage.Url(mail));
        Assert.Equal(mail,ProfileStartPage.WithUrl(mail," https://mail.proton.me/ "));
    }
    [Theory]
    [InlineData("https://other-mail.test/inbox")]
    [InlineData("http://localhost:8080/check")]
    [InlineData("https://[::1]:8443/")]
    public void Regular_profile_can_switch_to_any_valid_web_url_without_losing_metadata(string url)
    {
        using var env=new TestEnv();var mail=env.AddProfile();
        var web=ProfileStartPage.WithUrl(mail,url);
        Assert.Equal(mail.Id,web.Id);Assert.Equal(mail.DisplayName,web.DisplayName);Assert.Equal(mail.Color,web.Color);
        Assert.Equal(mail.Proxy,web.Proxy);Assert.Equal(ProfileKind.Test,web.Kind);Assert.Equal(url,web.TestStartUrl);
        Assert.Empty(ProfileValidator.Validate(web));Assert.True(ProfileConfig.RequiresRestart(mail,web));
        Assert.True(env.Catalog.SaveSettings(web,profileIsLive:false).Saved);
        Assert.Equal(web.TestStartUrl,env.Repository.Get(mail.Id)!.TestStartUrl);
        Assert.Equal(TopLevelDecision.Allow,new NavigationPolicy().ForProfile(web).EvaluateTopLevel("https://another.test/redirect"));
    }
    [Theory]
    [InlineData("file:///C:/test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@private.test/")]
    [InlineData("")]
    public void Invalid_url_edits_leave_the_saved_profile_unchanged(string url)
    {
        using var env=new TestEnv();var mail=env.AddProfile();
        Assert.Throws<ArgumentException>(()=>ProfileStartPage.WithUrl(mail,url));
        Assert.Equal(mail,env.Repository.Get(mail.Id));
    }
    [Fact]
    public void Stripped_custom_url_remains_unconfigured_on_import_and_in_editor()
    {
        using var env=new TestEnv();var web=ProfileStartPage.WithUrl(env.AddProfile(),"https://private.test/?token=secret");
        var exported=SettingsInterchange.Export([web]);Assert.DoesNotContain("secret",exported);
        var imported=SettingsInterchange.Import(Encoding.UTF8.GetBytes(exported));Assert.True(imported.Success);
        var p=imported.Preview!.Profiles[0];Assert.Equal(ProfileKind.Test,p.Kind);Assert.Equal(string.Empty,ProfileStartPage.Url(p));
        Assert.Equal(NetworkReadiness.TestUrlRequired,NetworkReadinessEvaluator.Evaluate(p,env.Engine.Capabilities,env.Credentials));
    }
}
