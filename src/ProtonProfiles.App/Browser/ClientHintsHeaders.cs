using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

/// <summary>Remove client hints at the native request boundary, including worker requests.</summary>
internal static class ClientHintsHeaders
{
    private static readonly ConditionalWeakTable<CoreWebView2, object> Configured = new();
    public static void Configure(CoreWebView2 core)
    {
        if (Configured.TryGetValue(core,out _)) return;
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_,e) =>
        {
            // Collect first: mutating a native collection while iterating it is unsafe.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var iterator = e.Request.Headers.GetIterator();
            while (iterator.HasCurrentHeader)
            {
                var name = iterator.Current.Key;
                if (StandardFingerprintPrivacy.IsClientHintHeader(name)) names.Add(name);
                iterator.MoveNext();
            }
            foreach (var name in names) e.Request.Headers.RemoveHeader(name);
        };
        Configured.Add(core,new object());
    }
}
