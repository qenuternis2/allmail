using System.Text;
using Json.Schema;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Tests;

public class InterchangeTests
{
    private static string SchemaDir => Path.Combine(AppContext.BaseDirectory, "schema");
    // Normalized so fixture edits do not depend on the checkout line endings (Windows autocrlf).
    private static string Example => File.ReadAllText(Path.Combine(SchemaDir, "profile-settings.example.json")).ReplaceLineEndings("\n");
    private static readonly Lazy<JsonSchema> LazySchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(SchemaDir, "profile-settings.schema.json"))));
    private static JsonSchema Schema => LazySchema.Value;

    private static bool SchemaValid(string json) =>
        Schema.Evaluate(System.Text.Json.JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;

    [Theory]
    [InlineData(GraphicsPolicy.BlockWebGlAndWebGpuExperimental)]
    [InlineData(GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental)]
    [InlineData(GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndScreenExperimental)]
    public void Test_profiles_and_graphics_match_schema_with_or_without_explicit_url_export(GraphicsPolicy policy)
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Test", Kind = ProfileKind.Test,
            TestStartUrl = "HTTPS://EXAMPLE.TEST:8443/check?a=1", GraphicsPolicy = policy };
        foreach (var include in new[] { false, true })
        {
            var json = SettingsInterchange.Export([p], new ExportOptions(IncludeTestStartUrls: include));
            Assert.True(SchemaValid(json), json);
            var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(json));
            Assert.True(imported.Success);
            Assert.Equal(include ? p.TestStartUrl : null, imported.Preview!.Profiles[0].TestStartUrl);
            Assert.Equal(p.GraphicsPolicy, imported.Preview.Profiles[0].GraphicsPolicy);
        }
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(SettingsInterchange.Export([p], new ExportOptions(IncludeTestStartUrls: true)))!;
        invalid["profiles"]![0]!["profileKind"] = "Mail";
        Assert.False(SchemaValid(invalid.ToJsonString()));
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(invalid.ToJsonString())).Success);
    }

    [Fact]
    public void Shipped_example_matches_schema_and_imports()
    {
        Assert.True(SchemaValid(Example));
        var r = SettingsInterchange.Import(Encoding.UTF8.GetBytes(Example));
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(2, r.Preview!.Profiles.Count);
        Assert.Equal(NetworkMode.System, r.Preview.Profiles[0].NetworkMode);
        Assert.Equal(NetworkMode.Unset, r.Preview.Profiles[1].NetworkMode);
        Assert.Equal("ru-RU", r.Preview.Profiles[0].LanguageTag);
        Assert.Equal(ScriptLocaleMode.MatchBrowserLanguage, r.Preview.Profiles[0].ScriptLocaleMode);
        Assert.Equal(ColorSchemePreference.Dark, r.Preview.Profiles[1].ColorScheme);
        Assert.NotEmpty(r.Preview.Notes);
    }

    [Fact]
    public void Import_assigns_fresh_ids_and_no_trust_or_session_state()
    {
        var a = SettingsInterchange.Import(Encoding.UTF8.GetBytes(Example)).Preview!;
        var b = SettingsInterchange.Import(Encoding.UTF8.GetBytes(Example)).Preview!;
        Assert.Empty(a.Profiles.Select(p => p.Id).Intersect(b.Profiles.Select(p => p.Id)));
        Assert.All(a.Profiles, p =>
        {
            Assert.NotEqual(Guid.Empty, p.Id);
            Assert.Null(p.LastOpenedAt);
            Assert.Null(p.LastUserConfirmedVisitAt);
            Assert.Null(p.DownloadDirectory);
            Assert.Null(p.WindowBounds);
            Assert.Null(p.Proxy?.CredentialRef);
        });
    }

    [Fact]
    public void Export_omits_secrets_ids_paths_and_matches_schema()
    {
        ProxyEndpoint.TryCreate("http", "proxy.internal.example", 3128, out var ep, out _);
        var id = Guid.NewGuid();
        var p = new ProfileConfig
        {
            Id = id, DisplayName = "Work", EmailLabel = "w@example.com", Color = "#123456",
            NetworkMode = NetworkMode.Proxy, Proxy = new ProxySettings(ep, ProxyAuthMode.Basic, "ProtonProfiles/proxy/secret-ref"),
            DownloadDirectory = @"C:\Users\me\Secret", WindowBounds = new WindowBounds(1, 2, 3, 4, false),
            BrowserTimeZoneId = "Europe/Berlin", LastUserConfirmedVisitAt = DateTimeOffset.UtcNow, UserAgentMode = UserAgentMode.Custom, CustomUserAgent = "UA/1",
        };
        var json = SettingsInterchange.Export([p]);
        Assert.True(SchemaValid(json), json);
        Assert.DoesNotContain(id.ToString(), json);
        Assert.DoesNotContain("secret-ref", json);
        Assert.DoesNotContain("Secret", json);
        Assert.DoesNotContain("proxy.internal.example", json); // endpoint excluded by default
        Assert.Contains("\"endpoint\": null", json);

        var withProxy = SettingsInterchange.Export([p], new ExportOptions(IncludeProxyEndpoints: true));
        Assert.True(SchemaValid(withProxy));
        Assert.Contains("proxy.internal.example", withProxy);
        Assert.DoesNotContain("secret-ref", withProxy);
    }

    [Fact]
    public void Roundtrip_preserves_preferences()
    {
        var original = SettingsInterchange.Import(Encoding.UTF8.GetBytes(Example)).Preview!.Profiles;
        var again = SettingsInterchange.Import(Encoding.UTF8.GetBytes(SettingsInterchange.Export(original))).Preview!.Profiles;
        Assert.Equal(original.Count, again.Count);
        for (var i = 0; i < original.Count; i++)
            Assert.Equal(original[i] with { Id = Guid.Empty }, again[i] with { Id = Guid.Empty });
    }

    [Theory]
    [InlineData(WebRtcPagePolicy.Block, WebRtcNetworkPolicy.RuntimeDefault)]
    [InlineData(WebRtcPagePolicy.Allow, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental)]
    public void Webrtc_preferences_match_schema_and_roundtrip(WebRtcPagePolicy page, WebRtcNetworkPolicy network)
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Synthetic", WebRtcPagePolicy = page, WebRtcNetworkPolicy = network };
        var exported = SettingsInterchange.Export([p]);
        Assert.True(SchemaValid(exported));
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(exported));
        Assert.True(imported.Success);
        Assert.Equal(page, imported.Preview!.Profiles[0].WebRtcPagePolicy);
        Assert.Equal(network, imported.Preview.Profiles[0].WebRtcNetworkPolicy);
        Assert.DoesNotContain("policyRevision", exported);
        Assert.DoesNotContain("guardRegistered", exported);
    }

    [Fact]
    public void Proxy_with_omitted_endpoint_imports_as_blocked_proxy_not_system()
    {
        var json = ReplaceFirst(Example, "\"mode\": \"System\"", "\"mode\": \"Proxy\", \"endpoint\": null, \"authMode\": \"Basic\"");
        Assert.True(SchemaValid(json));
        var r = SettingsInterchange.Import(Encoding.UTF8.GetBytes(json));
        Assert.True(r.Success, string.Join("\n", r.Errors));
        var p = r.Preview!.Profiles[0];
        Assert.Equal(NetworkMode.Proxy, p.NetworkMode);
        Assert.Null(p.Proxy!.Endpoint);
        Assert.Equal(ProxyAuthMode.Basic, p.Proxy.AuthMode);
    }

    public static TheoryData<string, string> Malformed => new()
    {
        { "unknown root field", "{\"schemaVersion\":1,\"profiles\":[],\"extra\":1}" },
        { "unknown version", "{\"schemaVersion\":2,\"profiles\":[]}" },
        { "string version", "{\"schemaVersion\":\"1\",\"profiles\":[]}" },
        { "duplicate key", "{\"schemaVersion\":1,\"schemaVersion\":1,\"profiles\":[]}" },
        { "not json", "{schemaVersion:1" },
        { "trailing comma", "{\"schemaVersion\":1,\"profiles\":[],}" },
        { "array root", "[]" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Rejects_malformed_documents(string _, string json)
    {
        var r = SettingsInterchange.Import(Encoding.UTF8.GetBytes(json));
        Assert.False(r.Success);
        Assert.Null(r.Preview);
    }

    public static TheoryData<string, string, string> InvalidValues => new()
    {
        { "unknown profile field", "\"isFavorite\": true", "\"isFavorite\": true, \"uuid\": \"x\"" },
        { "bad color", "\"#2563EB\"", "\"blue\"" },
        { "bad enum", "\"colorScheme\": \"Auto\"", "\"colorScheme\": \"System\"" },
        { "numeric enum", "\"colorScheme\": \"Auto\"", "\"colorScheme\": \"0\"" },
        { "zoom out of range", "\"zoomFactor\": 1.0,\n      \"trackingPreventionLevel\": \"Balanced\",\n      \"reminderMonths\": 6\n    },\n    {", "\"zoomFactor\": 9.0,\n      \"trackingPreventionLevel\": \"Balanced\",\n      \"reminderMonths\": 6\n    },\n    {" },
        { "reminder 13", "\"reminderMonths\": 6\n    }\n  ]", "\"reminderMonths\": 13\n    }\n  ]" },
        { "reminder fractional", "\"reminderMonths\": 6\n    }\n  ]", "\"reminderMonths\": 6.5\n    }\n  ]" },
        { "invalid locale", "\"tag\": \"ru-RU\"", "\"tag\": \"ru_RU!\"" },
        { "tag in system mode", "\"mode\": \"System\",\n        \"tag\": null", "\"mode\": \"System\",\n        \"tag\": \"en-US\"" },
        { "ua value in default mode", "\"mode\": \"Default\",\n        \"value\": null\n      },\n      \"language\": {\n        \"mode\": \"Custom\"", "\"mode\": \"Default\",\n        \"value\": \"X\"\n      },\n      \"language\": {\n        \"mode\": \"Custom\"" },
        { "empty name", "\"Example primary mailbox\"", "\"   \"" },
        { "socks proxy", "\"mode\": \"System\"", "\"mode\": \"Proxy\", \"endpoint\": {\"scheme\":\"socks5\",\"host\":\"h\",\"port\":1080}, \"authMode\": \"None\"" },
        { "port out of range", "\"mode\": \"System\"", "\"mode\": \"Proxy\", \"endpoint\": {\"scheme\":\"http\",\"host\":\"h\",\"port\":70000}, \"authMode\": \"None\"" },
        { "proxy missing authMode", "\"mode\": \"System\"", "\"mode\": \"Proxy\", \"endpoint\": null" },
        { "system with endpoint", "\"mode\": \"System\"", "\"mode\": \"System\", \"endpoint\": null" },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Rejects_invalid_values_without_partial_import(string _, string find, string replace)
    {
        Assert.Contains(find, Example);
        var json = ReplaceFirst(Example, find, replace);
        var r = SettingsInterchange.Import(Encoding.UTF8.GetBytes(json));
        Assert.False(r.Success);
        Assert.Null(r.Preview); // whole document rejected: no partial import
        if (!IsSemanticOnly(_)) Assert.False(SchemaValid(json), "schema accepted a document the validator rejects");
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        var i = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(i >= 0, "fixture text not found: " + find);
        return text[..i] + replace + text[(i + find.Length)..];
    }

    // Cases the JSON Schema cannot express; everything else must also fail schema validation.
    private static bool IsSemanticOnly(string name) => name is "duplicate key";

    [Fact]
    public void Rejects_oversized_file_and_too_many_records()
    {
        var big = new byte[SettingsInterchange.MaxFileBytes + 1];
        Array.Fill(big, (byte)' ');
        Assert.False(SettingsInterchange.Import(big).Success);

        var one = "{\"displayName\":\"P\",\"color\":\"#000000\",\"isFavorite\":false,\"network\":{\"mode\":\"System\"},\"userAgent\":{\"mode\":\"Default\",\"value\":null},\"language\":{\"mode\":\"System\",\"tag\":null},\"scriptLocale\":{\"mode\":\"Default\",\"tag\":null},\"colorScheme\":\"Auto\",\"zoomFactor\":1,\"trackingPreventionLevel\":\"Balanced\",\"reminderMonths\":6}";
        var ok = "{\"schemaVersion\":1,\"profiles\":[" + string.Join(",", Enumerable.Repeat(one, 1000)) + "]}";
        Assert.True(SettingsInterchange.Import(Encoding.UTF8.GetBytes(ok)).Success);
        var tooMany = "{\"schemaVersion\":1,\"profiles\":[" + string.Join(",", Enumerable.Repeat(one, 1001)) + "]}";
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(tooMany)).Success);
    }

    [Fact]
    public void Rejects_invalid_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes(Example).Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        Assert.False(SettingsInterchange.Import(bytes).Success);
    }

    [Fact]
    public void Catalog_commit_is_transactional_and_keeps_profiles_closed()
    {
        using var env = new TestEnv();
        env.AddProfile("Existing");
        var preview = env.Catalog.PreviewImport(Encoding.UTF8.GetBytes(Example));
        Assert.True(preview.Success);
        env.Catalog.CommitImport(preview.Preview!);
        var all = env.Catalog.List();
        Assert.Equal(3, all.Count);
        Assert.Equal(["Existing", "Example primary mailbox", "Example profile awaiting network setup"], all.Select(p => p.DisplayName));
        Assert.All(all, p => Assert.False(Directory.Exists(env.Paths.UserDataFolder(p.Id))));
    }
}
