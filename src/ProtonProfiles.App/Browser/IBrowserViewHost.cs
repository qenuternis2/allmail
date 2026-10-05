using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Permissions;

namespace ProtonProfiles.App.Browser;

public enum DownloadPhase { InProgress, Completed, Interrupted, Cancelled }

public sealed record DownloadInfo(GenerationContext Context, string FileName, DownloadPhase Phase, string? Reason);

/// <summary>UI services the engine needs. Every call carries the immutable generation context.</summary>
public interface IBrowserViewHost
{
    /// <summary>Places the main WebView of a generation into the window (initially hidden).</summary>
    void Attach(GenerationContext context, WebView2 view);
    void Detach(GenerationContext context, WebView2 view);
    /// <summary>Shows a tab only after its settings and guards have been installed.</summary>
    void TabReady(GenerationContext context, WebView2 view);
    WebView2? ActiveView(GenerationContext context);

    Task<UserPermissionAnswer?> AskPermissionAsync(GenerationContext context, string origin, PermissionKindKey kind);

    /// <summary>A blocked navigation that the user may explicitly open in the external browser.</summary>
    void OfferExternalLink(GenerationContext context, string uri);

    /// <summary>Asks where to save an attachment (after the event handler has returned). Returns null if the user cancels.</summary>
    Task<string?> ChooseDownloadPathAsync(GenerationContext context, string sanitizedFileName, string? initialDirectory);

    void ReportDownload(DownloadInfo info);

    void ReportProblem(GenerationContext context, string message);
    Task StopProfileAsync(GenerationContext context, string message);
}
