using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class PrivacyExceptionTests
{
    [Fact]
    public void Single_exceptions_remove_only_selected_browser_flags_and_preserve_proxy_routing()
    {
        ProxyEndpoint.TryCreate("http","::1",8080,out var proxy,out _);
        var all=BrowserArguments.Build(proxy,graphics:GraphicsPolicy.StrictFingerprintExperimental);
        foreach(var (feature,flag) in new[]{(PrivacyException.Graphics,BrowserArguments.GraphicsPolicyFlags),(PrivacyException.CanvasReadback,BrowserArguments.CanvasReadbackFlag)}) {
            var selected=BrowserArguments.Build(proxy,graphics:GraphicsPolicy.StrictFingerprintExperimental,exceptions:feature);
            Assert.DoesNotContain(flag,selected);
            Assert.Contains(feature==PrivacyException.Graphics?BrowserArguments.CanvasReadbackFlag:BrowserArguments.GraphicsPolicyFlags,selected);
            foreach(var preserved in new[]{BrowserArguments.WebRtcPolicyFlag,"--disable-quic","--proxy-server=http://[::1]:8080","--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE ::1\""})Assert.Contains(preserved,selected);
        }
        foreach(var (feature,blink) in new[]{(PrivacyException.SpeechSynthesis,"ScriptedSpeechSynthesis"),(PrivacyException.SharedWorkers,"SharedWorker"),(PrivacyException.LocalFonts,"FontAccess")}) {
            var selected=BrowserArguments.Build(proxy,graphics:GraphicsPolicy.StrictFingerprintExperimental,exceptions:feature);
            Assert.Contains(blink,all);Assert.DoesNotContain(blink,selected);
            Assert.Contains("RemotePlayback",selected);Assert.Contains(BrowserArguments.CanvasReadbackFlag,selected);
        }
    }
    [Fact]
    public void Exceptions_persist_independently_and_require_restart()
    {
        using var env=new TestEnv();var a=env.AddProfile("A");var b=env.AddProfile("B");
        var selected=a with {PrivacyExceptions=PrivacyException.WebAudio|PrivacyException.ServiceWorkers|PrivacyException.Camera|PrivacyException.CanvasTextMetrics};
        Assert.True(ProfileConfig.RequiresRestart(a,selected));env.Repository.Update(selected);
        var reopened=new SqliteProfileRepository(env.Paths.DatabasePath,env.Paths.BackupsRoot);
        Assert.Equal(selected.PrivacyExceptions,reopened.Get(a.Id)!.PrivacyExceptions);Assert.Equal(PrivacyException.None,reopened.Get(b.Id)!.PrivacyExceptions);
        var revision=reopened.SaveRevisionSnapshot(selected);Assert.Equal(selected.PrivacyExceptions,reopened.GetRevisionSnapshot(a.Id,revision)!.PrivacyExceptions);
    }
    [Fact]
    public void V5_migration_preserves_profiles_with_zero_exceptions_and_keeps_backup()
    {
        using var env=new TestEnv();var p=env.AddProfile();SqliteConnection.ClearAllPools();
        using(var db=new SqliteConnection($"Data Source={env.Paths.DatabasePath}")){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="ALTER TABLE Profile DROP COLUMN PrivacyExceptions; PRAGMA user_version=5; UPDATE ProfileRevision SET Snapshot=json_remove(Snapshot, '$.PrivacyExceptions');";cmd.ExecuteNonQuery();}
        var migrated=new SqliteProfileRepository(env.Paths.DatabasePath,env.Paths.BackupsRoot);
        Assert.Equal(PrivacyException.None,migrated.Get(p.Id)!.PrivacyExceptions);Assert.Equal(PrivacyException.None,migrated.GetRevisionSnapshot(p.Id,1)!.PrivacyExceptions);Assert.NotEmpty(Directory.GetFiles(env.Paths.BackupsRoot));
    }
    [Fact]
    public void Export_matches_schema_and_roundtrips_every_exception_without_secrets()
    {
        var p=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="A",PrivacyExceptions=ProfilePrivacy.KnownExceptions};
        var json=SettingsInterchange.Export([p]);
        Assert.True(InterchangeTests.SchemaValid(json),json);
        var imported=SettingsInterchange.Import(Encoding.UTF8.GetBytes(json));Assert.True(imported.Success,string.Join(";",imported.Errors));
        Assert.Equal(p.PrivacyExceptions,imported.Preview!.Profiles[0].PrivacyExceptions);Assert.DoesNotContain(p.Id.ToString(),json);
    }
    [Theory]
    [InlineData("[\"WebAudio\",\"WebAudio\"]")][InlineData("[\"None\"]")][InlineData("[\"Unknown\"]")][InlineData("[\"4\"]")][InlineData("[4]")][InlineData("\"WebAudio\"")][InlineData("[\"WebAudio, CanvasReadback\"]")]
    public void Import_rejects_unknown_duplicate_numeric_and_composite_exceptions(string value)
    {
        var root=JsonNode.Parse(SettingsInterchange.Export([new ProfileConfig{Id=Guid.NewGuid(),DisplayName="A"}]))!;
        root["profiles"]![0]!["privacyExceptions"]=JsonNode.Parse(value);
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(root.ToJsonString())).Success);
    }
    [Fact]
    public void Invalid_masks_are_rejected_and_device_exceptions_do_not_grant_permissions()
    {
        Assert.False(ProfilePrivacy.IsValid((PrivacyException)(1L<<20)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>BrowserArguments.Build(null,exceptions:(PrivacyException)(1L<<20)));
        var denied=AdditionalFingerprintPrivacy.PermissionsToDeny(PrivacyException.Camera).ToArray();
        Assert.DoesNotContain("camera",denied);Assert.DoesNotContain("camera-ptz",denied);Assert.Contains("microphone",denied);Assert.Contains("geolocation",denied);
        Assert.Contains("camera",AdditionalFingerprintPrivacy.PermissionsToDeny(PrivacyException.MediaDevices));
    }
}
