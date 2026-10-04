using System.Text.Json.Nodes;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;
public class MathImplementationPrivacyTests
{
    private static JsonObject Observation()=>JsonNode.Parse("""
        {"status": "Observed", "native": true, "referenceMatches": true, "vectors": 16, "values": ["3fef967b5aa8b974","40cf3286f4ca2958","405f5407e07e0932","4162f2ad4c9e14d6","3eafc314ad19faa7","401fc2b51a4228db","3fa5421457a49441","3f1631c56724ff44","42ad93a9ee2439f0","3fa3bbc8df1da607","414b36ac8da32e9c","3fc387cdcec1b63a","40d4575be716f225","411167c2916e2fba","3fcee5f22ebb0553","3e8901e1f9db84cd"]}
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
