using System.Text.Json;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Storage;

public sealed record BrowserTabsSnapshot(string[] Addresses, int ActiveIndex)
{
    public static BrowserTabsSnapshot Create(IEnumerable<string?> addresses, int activeIndex)
    {
        var result = new List<string>();
        var selected = 0;
        var index = 0;
        foreach (var address in addresses)
        {
            if (address is not null && (address == "about:blank" || NavigationPolicy.IsValidTestStartUrl(address))
                && !InternalPageHeaders.IsInternalUri(address) && BrowserAddress.TryNormalize(address, out var normalized))
            {
                if (index == activeIndex) selected = result.Count;
                result.Add(normalized);
            }
            index++;
        }
        return new(result.ToArray(), selected);
    }
}

/// <summary>Local URLs only; outside WebViewData so resetting website data preserves the tab list.</summary>
public sealed class BrowserTabsStore(ManagedPaths paths)
{
    private sealed record Document(int Version, string?[]? Addresses, int ActiveIndex);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };

    public BrowserTabsSnapshot? Load(Guid profileId)
    {
        var file = paths.TabsStateFile(profileId);
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16 * 1024 * 1024) return null;
            var document = JsonSerializer.Deserialize<Document>(stream, Json);
            return document is { Version: 1, Addresses: not null }
                ? BrowserTabsSnapshot.Create(document.Addresses, document.ActiveIndex) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(Guid profileId, BrowserTabsSnapshot snapshot)
    {
        var normalized = BrowserTabsSnapshot.Create(snapshot.Addresses, snapshot.ActiveIndex);
        var file = paths.TabsStateFile(profileId);
        Directory.CreateDirectory(paths.ProfileDirectory(profileId));
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Document(1, normalized.Addresses, normalized.ActiveIndex), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
