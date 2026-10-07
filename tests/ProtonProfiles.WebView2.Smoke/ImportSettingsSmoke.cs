using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProtonProfiles.App;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Real production import/preview dialogs after file selection; no OS picker automation or external sites.</summary>
internal static class ImportSettingsSmoke
{
    public static async Task RunAsync(string root, string runtimeVersion)
    {
        var paths = new ManagedPaths(Path.Combine(root, "import-ui")); paths.EnsureBaseDirectories();
        var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        var credentials = new InMemoryCredentialStore();
        var catalog = new ProfileCatalog(repository, credentials);
        var permissions = new PermissionPolicy(repository);
        using var updater = new MailfudGeoIpUpdater(paths);
        Require(catalog.Create("Existing", null, "#2563EB", out var original).Saved, "fixture profile");
        original = repository.Get(original!.Id)!;
        repository.SetPermission(new(original.Id, "https://example.invalid", PermissionKindKey.Notifications, PermissionChoice.Allow, DateTimeOffset.UtcNow));
        Directory.CreateDirectory(paths.UserDataFolder(original.Id));
        var marker = Path.Combine(paths.UserDataFolder(original.Id), "session-marker"); File.WriteAllText(marker, "preserved");
        var shell = new MainWindow(paths, repository, catalog, credentials, permissions, runtimeVersion, updater)
            { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        shell.Initialize(new WebView2Engine(shell, paths, permissions, new NavigationPolicy(), credentials)); shell.Show();
        var lifecycle = (ProfileLifecycleService)typeof(MainWindow).GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        var method = typeof(MainWindow).GetMethod("ImportSettingsFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = original with { DisplayName = "Imported system" };
        var second = first with { Id = Guid.NewGuid(), DisplayName = "Imported unresolved proxy", NetworkMode = NetworkMode.Proxy,
            Proxy = new ProxySettings(null, ProxyAuthMode.Basic, "not-exported") };
        var third = first with { Id = Guid.NewGuid(), DisplayName = "Imported omitted network", NetworkMode = NetworkMode.Unset };
        var valid = SettingsInterchange.Export([first, second, third]);
        var selected = Path.Combine(paths.Root, "selected.json");
        var rejected = 0;
        try
        {
            void Unchanged()
            {
                Require(repository.ListProfiles().Count == 1 && repository.Get(original.Id) == original, "no partial metadata import");
                Require(repository.ListPermissions(original.Id).Count == 1 && File.ReadAllText(marker) == "preserved", "existing grants/session preserved");
                Require(lifecycle.LiveProfiles().Count == 0, "import never opens browser");
            }
            void Invoke(string title, string? button, string text, Action<Window>? inspect = null)
            {
                Exception? failure = null;
                var observed = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.Owner, shell));
                    if (dialog is null) return;
                    timer.Stop(); observed = true;
                    try
                    {
                        Unchanged(); dialog.UpdateLayout();
                        Require(dialog.Title == title && Visuals(dialog).OfType<TextBlock>().Any(b => b.Text.Contains(text, StringComparison.Ordinal)), "actual dialog title/message: " + title);
                        inspect?.Invoke(dialog);
                        if (button is null) dialog.Close();
                        else Visuals(dialog).OfType<Button>().Single(b => Equals(b.Content, button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                };
                timer.Start();
                try { method.Invoke(shell, [selected]); }
                finally { timer.Stop(); }
                Require(observed && failure is null, "dialog observation: " + failure);
            }
            void Reject(byte[] bytes, string title = "Импорт отклонён", string message = "Ничего не импортировано.")
            {
                File.WriteAllBytes(selected, bytes); Invoke(title, "ОК", message); Unchanged(); rejected++;
            }
            foreach (var malformed in new[] { "{schemaVersion:1", "[]", "{\"schemaVersion\":1,\"profiles\":[],}",
                "{\"schemaVersion\":1,\"schemaVersion\":1,\"profiles\":[]}" }) Reject(Encoding.UTF8.GetBytes(malformed));
            var mutations = new Action<JsonNode>[]
            {
                n => n["extra"] = true,
                n => n["schemaVersion"] = 2,
                n => n["schemaVersion"] = "1",
                n => n["profiles"]![1]!["uuid"] = "untrusted",
                n => n["profiles"]![1]!["color"] = "blue",
                n => n["profiles"]![1]!["displayName"] = " ",
                n => n["profiles"]![1]!["colorScheme"] = "Unknown",
                n => n["profiles"]![1]!["colorScheme"] = "0",
                n => n["profiles"]![1]!["zoomFactor"] = 9,
                n => n["profiles"]![1]!["reminderMonths"] = 6.5,
                n => n["profiles"]![1]!["reminderMonths"] = 13,
                n => n["profiles"]![1]!["language"] = new JsonObject { ["mode"] = "Custom", ["tag"] = "ru_RU!" },
                n => n["profiles"]![1]!["language"] = new JsonObject { ["mode"] = "System", ["tag"] = "en-US" },
                n => n["profiles"]![1]!["userAgent"] = new JsonObject { ["mode"] = "Default", ["value"] = "custom UA" },
                n => n["profiles"]![1]!["network"] = new JsonObject { ["mode"] = "Proxy", ["authMode"] = "None",
                    ["endpoint"] = new JsonObject { ["scheme"] = "socks5", ["host"] = "h", ["port"] = 1080 } },
                n => n["profiles"]![1]!["network"]!["endpoint"] = new JsonObject { ["scheme"] = "http", ["host"] = "h", ["port"] = 70000 },
                n => n["profiles"]![1]!["network"]!.AsObject().Remove("authMode"),
                n => n["profiles"]![1]!["network"] = new JsonObject { ["mode"] = "System", ["endpoint"] = null },
                n => n["profiles"]![1]!.AsObject().Remove("isFavorite"),
                n => n["profiles"] = new JsonArray(Enumerable.Range(0, 1001).Select(_ => n["profiles"]![0]!.DeepClone()).ToArray()),
            };
            foreach (var mutate in mutations) { var node = JsonNode.Parse(valid)!; mutate(node); Reject(Encoding.UTF8.GetBytes(node.ToJsonString())); }
            Reject(Encoding.UTF8.GetBytes(valid).Concat(new byte[] { 0xc3, 0x28 }).ToArray());
            Reject(Encoding.UTF8.GetBytes(valid.Replace("\"zoomFactor\": 1", "\"zoomFactor\": 1e999", StringComparison.Ordinal)));
            var duplicate = valid.Replace("\"displayName\": \"Imported unresolved proxy\"", "\"displayName\": \"Imported unresolved proxy\", \"displayName\": \"duplicate\"", StringComparison.Ordinal);
            Require(duplicate != valid, "duplicate nested key fixture"); Reject(Encoding.UTF8.GetBytes(duplicate));
            Reject(new byte[checked((int)SettingsInterchange.MaxFileBytes + 1)], "Импорт", "Файл больше 1 МиБ.");
            File.WriteAllText(selected, valid);
            Invoke("Предпросмотр импорта", "Отмена", "Будет создано профилей: 3.", dialog =>
                Require(Visuals(dialog).OfType<Button>().Single(b => Equals(b.Content, "Отмена")).IsCancel, "preview cancel action")); Unchanged();
            Invoke("Предпросмотр импорта", null, "Будет создано профилей: 3."); Unchanged();
            Console.WriteLine($"PASS: production import rejection matrix ({rejected} files), preview cancel/close; existing metadata, session marker and permissions unchanged.");
            // Genuine post-selection I/O failures: original handler throws rather than showing rejection.
            File.Delete(selected); Invoke("Импорт отклонён", "ОК", "Не удалось прочитать файл"); Unchanged();
            File.WriteAllText(selected, valid);
            using (var locked = new FileStream(selected, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { Invoke("Импорт отклонён", "ОК", "Не удалось прочитать файл"); Unchanged(); }
            Invoke("Предпросмотр импорта", "Импортировать", "Сессии, разрешения и учётные данные не переносятся.");
            var imported = repository.ListProfiles().Where(p => p.Id != original.Id).ToArray();
            Require(imported.Length == 3 && imported.Select(p => p.Id).Distinct().Count() == 3
                && !imported.Any(p => new[] { first.Id, second.Id, third.Id }.Contains(p.Id)), "fresh profile identities");
            foreach (var profile in imported)
            {
                Require(!Directory.Exists(paths.UserDataFolder(profile.Id)) && repository.ListPermissions(profile.Id).Count == 0
                    && profile.Proxy?.CredentialRef is null && profile.LastOpenedAt is null && profile.LastAppliedRevision is null
                    && lifecycle.GetState(profile.Id).Phase == LifecyclePhase.Closed, "no browser data, credentials, grants or activation transfer");
                Require(repository.GetRevisionSnapshot(profile.Id, profile.ConfigRevision) is not null, "initial revision snapshot");
            }
            foreach (var profile in imported.Where(p => p.NetworkMode != NetworkMode.System))
                Require((await lifecycle.OpenAsync(profile.Id)).Outcome == OpenOutcome.Blocked, "incomplete network blocked before native startup");
            Require(repository.Get(original.Id) == original && repository.ListPermissions(original.Id).Count == 1
                && File.ReadAllText(marker) == "preserved" && lifecycle.LiveProfiles().Count == 0, "existing profile remains untouched");
            Require(((ListBox)shell.FindName("ProfileList")).Items.Count == 4, "sidebar refresh after import");
            Console.WriteLine($"PASS: production import dialogs; {rejected} malformed/oversized files reject whole document; preview cancel/close unchanged; missing/locked file handled; confirmed import fresh UUIDs/revision snapshots, lazy UDFs, no grants/secrets/autostart, incomplete network blocked; existing profile intact.");
        }
        finally { shell.Close(); }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Visuals(child)) yield return descendant; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Import UI regression: " + message); }
}
