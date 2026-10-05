using System.Text.Json;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class BrowserPermissionScopeTests
{
    [Fact]
    public void Named_context_permissions_preserve_the_exact_context_and_descriptor()
    {
        const string context = "profile-context-\"quoted\"";
        using var value = JsonDocument.Parse(AdditionalFingerprintPrivacy.PermissionArguments("camera-ptz", context));
        Assert.Equal(context, value.RootElement.GetProperty("browserContextId").GetString());
        Assert.Equal("denied", value.RootElement.GetProperty("setting").GetString());
        Assert.Equal("camera", value.RootElement.GetProperty("permission").GetProperty("name").GetString());
        Assert.True(value.RootElement.GetProperty("permission").GetProperty("panTiltZoom").GetBoolean());
    }

    [Fact]
    public void Default_context_omits_the_optional_scope_instead_of_sending_null()
    {
        using var value = JsonDocument.Parse(AdditionalFingerprintPrivacy.PermissionArguments("geolocation"));
        Assert.False(value.RootElement.TryGetProperty("browserContextId", out _));
        Assert.Equal("geolocation", value.RootElement.GetProperty("permission").GetProperty("name").GetString());
    }
}
