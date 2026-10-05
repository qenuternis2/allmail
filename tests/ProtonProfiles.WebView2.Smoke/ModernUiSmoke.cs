using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProtonProfiles.App;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Dialogs;
using ProtonProfiles.App.ViewModels;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Render the production XAML/dialogs with production resources and isolated metadata. No external sites.</summary>
internal static class ModernUiSmoke
{
    public static async Task RunAsync(string root, string runtimeVersion)
    {
        var paths = new ManagedPaths(Path.Combine(root, "modern-ui")); paths.EnsureBaseDirectories();
        var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        var credentials = new InMemoryCredentialStore();
        var catalog = new ProfileCatalog(repository, credentials);
        var permissions = new PermissionPolicy(repository);
        using var updater = new MailfudGeoIpUpdater(paths);
        var work = repository.CreateGroup("Работа");
        var personal = repository.CreateGroup("Личное");
        ProfileConfig? original = null;
        var names = new[] { "Proton · работа", "Личная почта", "Cloudflare", "Профиль с очень длинным названием для проверки обрезки текста", "Новости" };
        var colors = new[] { "#0060DF", "#9059FF", "#D25C16", "#188572", "#D04273" };
        for (var i = 0; i < names.Length; i++)
        {
            var saved = catalog.Create(names[i], i == 0 ? "work@example.invalid" : null, colors[i], out var profile, groupId: i == 0 ? work.Id : personal.Id);
            Require(saved.Saved && profile is not null, "fixture metadata creation");
            if (i == 0)
            {
                original = profile! with { GraphicsPolicy = GraphicsPolicy.StrictFingerprintExperimental, PrivacyExceptions = PrivacyException.CanvasTextMetrics | PrivacyException.NativeMath, IsFavorite = true };
                repository.Update(original);
            }
        }
        var shell = new MainWindow(paths, repository, catalog, credentials, permissions, runtimeVersion, updater) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var engine = new WebView2Engine(shell, paths, permissions, new NavigationPolicy(), credentials);
        shell.Initialize(engine); shell.Show();
        try
        {
            var list = (ListBox)shell.FindName("ProfileList");
            var search = (TextBox)shell.FindName("SearchBox");
            var groups = (ComboBox)shell.FindName("GroupFilter");
            list.SelectedIndex = 0;
            await Layout(shell);
            Require(list.Items.Count == 5 && list.SelectedItem is ProfileItem { DisplayName: "Proton · работа" }, "profile selection");
            Capture(shell, "modern-main");
            foreach (var size in new[] { (900d, 560d), (1280d, 820d) })
            {
                shell.Width = size.Item1; shell.Height = size.Item2; await Layout(shell);
                foreach (var button in Visuals(shell).OfType<Button>().Where(b => b.IsVisible)) RequireInside(button, shell);
                Require(((TextBlock)shell.FindName("SelectedName")).ActualWidth > 80, "profile title at minimum width");
                if (size.Item1 == 900) Capture(shell, "modern-main-compact");
            }
            search.Text = "cloudflare"; await Layout(shell);
            Require(list.Items.Count == 1 && ((ProfileItem)list.Items[0]).DisplayName == "Cloudflare", "search in modern sidebar");
            search.Text = ""; groups.SelectedIndex = 2; await Layout(shell);
            Require(list.Items.Count == 1, "group filter in modern sidebar");
            groups.IsDropDownOpen = true; await Layout(shell);
            Require(groups.Template.FindName("PART_Popup", groups) is Popup { IsOpen: true }, "styled ComboBox popup");
            groups.IsDropDownOpen = false; groups.SelectedIndex = 0; list.SelectedIndex = 0;
            foreach (var name in new[] { "Действия профиля", "Управление профилями" })
            {
                var button = Visuals(shell).OfType<Button>().Single(b => Equals(AutomationName(b), name));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(shell);
                Require(button.ContextMenu is { IsOpen: true, PlacementTarget: var target } && ReferenceEquals(target, button), "production overflow menu opens");
                Require(button.ContextMenu!.Items.OfType<MenuItem>().Any(i => i.Header is string), "overflow actions retained");
                button.ContextMenu.IsOpen = false;
            }
            var created = new NewProfileWindow(shell, "#0060DF", repository.ListGroups(), work.Id);
            Exception? modalFailure = null;
            _ = created.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    created.UpdateLayout(); Capture(created, "modern-new-profile");
                    var inputs = Visuals(created).OfType<TextBox>().ToArray();
                    Require(inputs.Length == 3 && inputs[0].IsKeyboardFocusWithin, "creation keyboard focus");
                    var create = Visuals(created).OfType<Button>().Single(b => Equals(b.Content, "Создать профиль"));
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(created.Result is null && created.IsVisible, "invalid profile keeps dialog open");
                    inputs[0].Text = "Новая почта"; inputs[2].Text = "https://example.invalid/inbox";
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception e) { modalFailure = e; created.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            Require(created.ShowDialog() == true && modalFailure is null && created.Result?.DisplayName == "Новая почта" && created.Result.TestStartUrl == "https://example.invalid/inbox" && created.GroupId == work.Id, "creation and custom URL/group selection: " + modalFailure);

            var editor = new ProfileEditorWindow(shell, original!, engine.Capabilities, paths, updater);
            modalFailure = null;
            _ = editor.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    editor.UpdateLayout();
                    var settings = Visuals(editor).OfType<TabControl>().Single();
                    Require(settings.Items.Count == 5, "settings categories");
                    for (var i = 0; i < settings.Items.Count; i++)
                    {
                        settings.SelectedIndex = i; editor.UpdateLayout();
                        Capture(editor, "modern-settings-" + i);
                        var save = Visuals(editor).OfType<Button>().Single(b => Equals(b.Content, "Сохранить"));
                        RequireInside(save, editor);
                    }
                    editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; editor.UpdateLayout();
                    Capture(editor, "modern-settings-compact");
                    RequireInside(Visuals(editor).OfType<Button>().Single(b => Equals(b.Content, "Сохранить")), editor);
                    Visuals(editor).OfType<Button>().Single(b => Equals(b.Content, "Сохранить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (editor.IsVisible) throw new InvalidOperationException("settings save failed: " + string.Join(" / ", Visuals(editor).OfType<TextBlock>().Select(t => t.Text)));
                }
                catch (Exception e) { modalFailure = e; editor.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            Require(editor.ShowDialog() == true && modalFailure is null, "settings save: " + modalFailure);
            Require(editor.Result == original && editor.NewCredential is null, "settings categories preserve every profile value and exception");
            var groupWindow = new ProfileGroupsWindow(shell, repository) { Left = -10000, Top = -10000 };
            groupWindow.Show(); await Layout(groupWindow);
            Require(Visuals(groupWindow).OfType<ListBox>().Single().Items.Count == 2, "group manager list");
            Capture(groupWindow, "modern-groups"); groupWindow.Close();
            Require(repository.ListProfiles().Count == 5, "isolated UI checks don't modify stored profiles");
            Console.WriteLine("PASS: native modern WPF UI; production theme/main XAML and dialogs; 1280/900 layouts; search/group filter/overflow menus; keyboard focus and validation; custom URL/group creation; five settings categories and unchanged-value save; screenshots captured.");
        }
        finally { shell.Close(); }
    }

    private static object AutomationName(DependencyObject element) => element.GetValue(System.Windows.Automation.AutomationProperties.NameProperty);
    private static async Task Layout(FrameworkElement element) { await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }
    private static void RequireInside(FrameworkElement control, Window window)
    {
        var point = control.TranslatePoint(new Point(), window);
        Require(control.ActualWidth > 0 && control.ActualHeight > 0 && point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= window.ActualWidth + 1 && point.Y + control.ActualHeight <= window.ActualHeight + 1, "visible control inside window: " + control.GetType().Name);
    }
    private static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath("artifacts/test-results"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Modern UI regression: " + message); }
}
