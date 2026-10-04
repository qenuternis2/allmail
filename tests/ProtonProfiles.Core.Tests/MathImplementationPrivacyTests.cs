using System.Text.Json.Nodes;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;
public class MathImplementationPrivacyTests
{
    private static JsonObject Observation()=>JsonNode.Parse("""
        {"status": "Observed", "native": true, "referenceMatches": true, "vectors": 16, "values": ["3fcc8576b9821290", "45c51f96ddfe1294", "4067d365369167d6", "408967e63d6967c0", "3f54ba2f365f4928", "3cf50efe4c0f072a", "3fdb66f934e8c7a4", "405578cdac450aa4", "3ff3dd7d668fcde4", "3fee369efcbec66e", "40e70b81d193cd0e", "4012665d63588a80", "4008e5671ac17260", "3f04e38b44ae1118", "3f6df8f995c43c2c", "3ffa80e859564e22"]}
        """)!.AsObject();
    [Fact]
    public void Reference_vectors_must_match_actual_bits_even_if_boolean_claims_success()
    {
        var o=Observation();Assert.Equal(GraphicsReadbackOutcome.Verified,MathImplementationPrivacy.ReadResult(o.ToJsonString()));
        o["values"]![0]="0000000000000000";Assert.Equal(GraphicsReadbackOutcome.Violation,MathImplementationPrivacy.ReadResult(o.ToJsonString()));
        o=Observation();o["native"]=false;Assert.Equal(GraphicsReadbackOutcome.Violation,MathImplementationPrivacy.ReadResult(o.ToJsonString()));
    }
    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("{broken")]
    public void Missing_and_malformed_math_observations_do_not_verify(string? json)=>Assert.Equal(GraphicsReadbackOutcome.Unavailable,MathImplementationPrivacy.ReadResult(json));
    [Fact]
    public void Complete_shape_and_explicit_booleans_are_required()
    {
        foreach(var key in new[]{"status","native","referenceMatches","vectors","values"}) {
            var o=Observation();o.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,MathImplementationPrivacy.ReadResult(o.ToJsonString()));
        }
        var wrong=Observation();wrong["native"]="true";Assert.Equal(GraphicsReadbackOutcome.Unavailable,MathImplementationPrivacy.ReadResult(wrong.ToJsonString()));
        wrong=Observation();wrong["values"]![0]="missing";Assert.Equal(GraphicsReadbackOutcome.Unavailable,MathImplementationPrivacy.ReadResult(wrong.ToJsonString()));
        var profile=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="A",GraphicsPolicy=GraphicsPolicy.StrictFingerprintExperimental};
        Assert.True(MathImplementationPrivacy.IsEnabled(profile));Assert.False(MathImplementationPrivacy.IsEnabled(profile with{PrivacyExceptions=PrivacyException.NativeMath}));
    }
}
