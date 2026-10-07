using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProtonProfiles.App;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Production final-path/dialog behavior after the native file picker; synthetic files only.</summary>
internal static class DownloadSavePathSmoke
{
    public static void Run(string root, string runtimeVersion)
    {
        var paths = new ManagedPaths(Path.Combine(root, "download-save-ui"));
        paths.EnsureBaseDirectories();
        var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        var credentials = new InMemoryCredentialStore();
        var catalog = new ProfileCatalog(repository, credentials);
        var permissions = new PermissionPolicy(repository);
        using var updater = new MailfudGeoIpUpdater(paths);
        var shell = new MainWindow(paths, repository, catalog, credentials, permissions, runtimeVersion, updater)
            { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        shell.Initialize(new WebView2Engine(shell, paths, permissions, new NavigationPolicy(), credentials));
        shell.Show();
        var method = typeof(MainWindow).GetMethod("FinalizeDownloadPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var directory = Path.Combine(paths.Root, "chosen-folder");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (edited, finalName) in new[] { ("e\u0301.txt", "é.txt"), ("report\u202E.txt", "report_.txt") })
            {
                var target = Path.Combine(directory, edited);
                var final = Path.Combine(directory, finalName);
                File.WriteAllText(final, "original contents");
                Require(!File.Exists(target), "picker target differs from existing final file");
                foreach (var action in new string?[] { "Отмена", null, "Заменить" })
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
                            dialog.UpdateLayout();
                            Require(dialog.Title == "Заменить существующий файл?", "actual overwrite dialog");
                            Require(Visuals(dialog).OfType<TextBlock>().Any(t => t.Text.Contains(finalName, StringComparison.Ordinal)), "final filename shown");
                            var cancel = Visuals(dialog).OfType<Button>().Single(b => Equals(b.Content, "Отмена"));
                            var replace = Visuals(dialog).OfType<Button>().Single(b => Equals(b.Content, "Заменить"));
                            Require(cancel.IsDefault && cancel.IsCancel && !replace.IsDefault, "cancel is safe default and Escape action");
                            if (action is null) dialog.Close();
                            else (action == "Отмена" ? cancel : replace).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception error) { failure = error; dialog.Close(); }
                    };
                    timer.Start();
                    string? result;
                    try { result = (string?)method.Invoke(shell, [target]); }
                    finally { timer.Stop(); }
                    Require(observed && failure is null, "confirmation observed: " + failure);
                    Require(result == (action == "Заменить" ? final : null), "only explicit replacement returns final path");
                    Require(File.ReadAllText(final) == "original contents" && !File.Exists(target), "path selection never writes either file");
                }
            }
            foreach (var (edited, finalName, existing) in new[]
            {
                ("ordinary.txt", "ordinary.txt", true),
                ("fresh.txt", "fresh.txt", false),
                ("cafe\u0301-new.txt", "café-new.txt", false),
            })
            {
                var final = Path.Combine(directory, finalName);
                if (existing) File.WriteAllText(final, "already confirmed by picker");
                var unexpectedDialog = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.Owner, shell));
                    if (dialog is null) return;
                    unexpectedDialog = true; dialog.Close();
                };
                timer.Start();
                object? result;
                try { result = method.Invoke(shell, [Path.Combine(directory, edited)]); }
                finally { timer.Stop(); }
                Require(Equals(result, final) && !unexpectedDialog, "ordinary/fresh path preserved without extra dialog");
                if (existing) Require(File.ReadAllText(final) == "already confirmed by picker", "existing ordinary file preserved");
                else Require(!File.Exists(final), "selection does not create file");
            }
            Console.WriteLine("PASS: production download final-path confirmation; NFC/bidi collisions cancel, close and replace; final name shown; Cancel default/Escape; ordinary/fresh targets unchanged; nine synthetic cases, files untouched; native picker not automated.");
        }
        finally { shell.Close(); }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Visuals(child)) yield return descendant; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Download save UI regression: " + message); }
}
