using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

public static class ProfilePrivacy
{
    public static readonly PrivacyException KnownExceptions=Enum.GetValues<PrivacyException>().Aggregate(PrivacyException.None,(a,b)=>a|b);
    public static bool IsValid(PrivacyException value)=>(value & ~KnownExceptions)==0;
    public static bool Allows(PrivacyException value,PrivacyException feature)=>(value & feature)==feature;
    public static bool Allows(ProfileConfig config,PrivacyException feature)=>Allows(config.PrivacyExceptions,feature);
    public static string[] Names(PrivacyException value)
    {
        if(!IsValid(value))throw new ArgumentOutOfRangeException(nameof(value));
        return Enum.GetValues<PrivacyException>().Where(v=>v!=PrivacyException.None&&Allows(value,v)).Select(v=>v.ToString()).ToArray();
    }
    public static bool BlockGraphics(ProfileConfig config)=>config.GraphicsPolicy!=GraphicsPolicy.RuntimeDefault&&!Allows(config,PrivacyException.Graphics);
    public static bool BlockCanvas(ProfileConfig config)=>(int)config.GraphicsPolicy>=2&&!Allows(config,PrivacyException.CanvasReadback);
    public static bool BlockServiceWorkers(ProfileConfig config)=>ResidualFingerprintPrivacy.IsEnabled(config.GraphicsPolicy)&&!Allows(config,PrivacyException.ServiceWorkers);
}
