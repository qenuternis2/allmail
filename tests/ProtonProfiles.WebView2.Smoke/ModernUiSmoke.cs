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
using ProtonProfiles.Core.Lifecycle;
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
        var lifecycle=(ProfileLifecycleService)typeof(MainWindow).GetField("_lifecycle",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(shell)!;
        using var site=new GeoIpTimeZoneSmoke.IpServer(false,html:true);
        var liveIds=new List<Guid>();
        WindowBounds? beforeClosing = null;
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
            groups.IsDropDownOpen = false;
            var column = (ColumnDefinition)shell.FindName("ProfilesColumn");
            var sidebar = (FrameworkElement)shell.FindName("ProfilesPanel");
            var strip = (FrameworkElement)shell.FindName("CollapsedProfilesStrip");
            var splitter = (GridSplitter)shell.FindName("ProfilesSplitter");
            var collapse = (Button)shell.FindName("CollapseProfilesButton");
            var expand = (Button)shell.FindName("ExpandProfilesButton");
            var browserArea = (FrameworkElement)shell.FindName("BrowserArea");
            foreach (var size in new[] { (900d, 560d), (1280d, 820d) })
            {
                shell.Width = size.Item1; shell.Height = size.Item2;
                column.Width = new GridLength(360); await Layout(shell);
                var selection = list.SelectedItem; var filter = groups.SelectedItem;
                var count = list.Items.Count; var query = search.Text;
                var browserWidth = browserArea.ActualWidth;
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(shell);
                    Require(sidebar.Visibility == Visibility.Collapsed && splitter.Visibility == Visibility.Collapsed
                        && strip.IsVisible && Math.Abs(column.ActualWidth - 40) < 1, "sidebar becomes narrow strip");
                    Require(browserArea.ActualWidth > browserWidth + 300, "collapsed sidebar gives space to browser");
                    RequireInside(expand, shell);
                    Require(expand.IsKeyboardFocusWithin, "collapse transfers keyboard focus to restore button");
                    if (cycle == 0) Capture(shell, size.Item1 == 900 ? "modern-sidebar-collapsed-compact" : "modern-sidebar-collapsed");
                    expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(shell);
                    Require(sidebar.IsVisible && splitter.IsVisible && !strip.IsVisible
                        && Math.Abs(column.ActualWidth - 360) < 1 && column.MinWidth == 250, "restore resized sidebar and splitter limits");
                    Require(ReferenceEquals(list.SelectedItem, selection) && ReferenceEquals(groups.SelectedItem, filter)
                        && list.Items.Count == count && search.Text == query, "sidebar toggle preserves selection/search/group filter");
                }
            }
            column.Width = new GridLength(288); await Layout(shell);
            groups.SelectedIndex = 0; list.SelectedIndex = 0;
            foreach (var name in new[] { "Действия профиля", "Управление профилями" })
            {
                var button = Visuals(shell).OfType<Button>().Single(b => Equals(AutomationName(b), name));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(shell);
                Require(button.ContextMenu is { IsOpen: true, PlacementTarget: var target } && ReferenceEquals(target, button), "production overflow menu opens");
                Require(button.ContextMenu!.Items.OfType<MenuItem>().Any(i => i.Header is string), "overflow actions retained");
                button.ContextMenu.IsOpen = false;
            }
            var otherId=repository.ListProfiles().First(p=>p.Id!=original!.Id).Id;
            const string permissionOrigin="https://permission-fixture.invalid";
            repository.SetPermission(new(original!.Id,permissionOrigin,PermissionKindKey.Notifications,PermissionChoice.Allow,DateTimeOffset.UtcNow));
            repository.SetPermission(new(otherId,permissionOrigin,PermissionKindKey.Notifications,PermissionChoice.Allow,DateTimeOffset.UtcNow));
            Directory.CreateDirectory(paths.UserDataFolder(original.Id));
            var cookieMarker=Path.Combine(paths.UserDataFolder(original.Id),"session-marker");File.WriteAllText(cookieMarker,"preserved");
            new BrowserTabsStore(paths).Save(original.Id,new(["https://example.invalid/"],0));
            _ = shell.Dispatcher.BeginInvoke(new Action(()=>{
                var dialog=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="Сброс разрешений");
                Visuals(dialog).OfType<Button>().Single(b=>Equals(b.Content,"Сбросить разрешения")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }),DispatcherPriority.ApplicationIdle);
            ((MenuItem)shell.FindName("ResetPermissionsButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(repository.ListPermissions(original.Id).Count==0 && repository.ListPermissions(otherId).Count==1
                && File.ReadAllText(cookieMarker)=="preserved" && new BrowserTabsStore(paths).Load(original.Id)!.Addresses.Length==1,
                "permission reset is per-profile and preserves browser data/tab file");
            Console.WriteLine("PASS: production reset-permissions menu and confirmation; A choices removed, B retained; UDF/session marker and saved tabs preserved.");
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
            for(var i=0;i<100;i++) catalog.Create("Perf fixture "+i,null,"#0060DF",out _);
            foreach(var profile in repository.ListProfiles().Where(p=>p.DisplayName.StartsWith("Perf fixture ",StringComparison.Ordinal)).Take(3))
            {
                repository.Update(ProfileStartPage.WithUrl(profile,$"http://127.0.0.1:{site.Port}/index"));
                liveIds.Add(profile.Id);
                Require((await lifecycle.OpenAsync(profile.Id)).Outcome==OpenOutcome.Opened,"real browser opened in 105-profile directory");
            }
            Require(lifecycle.LiveProfiles().Count==3,"three real profile environments for UI benchmark");
            var fourth=repository.ListProfiles().First(p=>p.DisplayName.StartsWith("Perf fixture ",StringComparison.Ordinal)&&!liveIds.Contains(p.Id));
            Require((await lifecycle.OpenAsync(fourth.Id)).Outcome==OpenOutcome.CapacityReached,"directory does not open a fourth real environment");
            typeof(MainWindow).GetMethod("Reload",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(shell,null);
            // Opening an environment starts navigation; it does not mean its page
            // or first native HWND frame has loaded. Report that startup separately.
            var firstSwitches=new List<double>();
            foreach(var id in liveIds)
            {
                var session=(WebView2Session)lifecycle.GetSession(id)!;
                for(var attempt=0;await session.Views[0].CoreWebView2.ExecuteScriptAsync("document.readyState==='complete'&&document.title==='A18 fixture'")!="true";attempt++)
                {
                    if(attempt>=100)throw new TimeoutException("Directory fixture page did not load.");
                    await Task.Delay(25);
                }
                var firstSwitch=System.Diagnostics.Stopwatch.StartNew();
                list.SelectedItem=list.Items.OfType<ProfileItem>().Single(p=>p.Id==id);
                await Layout(shell);firstSwitch.Stop();firstSwitches.Add(firstSwitch.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("Directory first loaded-page switches, including initial native frame (ms): "+string.Join(", ",firstSwitches.Select(value=>value.ToString("F2",System.Globalization.CultureInfo.InvariantCulture))));
            var selectedLiveId = ((ProfileItem)list.SelectedItem).Id;
            var liveSession = (WebView2Session)lifecycle.GetSession(selectedLiveId)!;
            var liveView = liveSession.Views[0];
            await liveView.CoreWebView2.ExecuteScriptAsync("window.sidebarMarker='preserved'");
            foreach (var button in new[] { collapse, expand })
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout(shell);
                Require(ReferenceEquals(lifecycle.GetSession(selectedLiveId), liveSession) && lifecycle.LiveProfiles().Count == 3
                    && ReferenceEquals(((IBrowserViewHost)shell).ActiveView(new(selectedLiveId, lifecycle.GetState(selectedLiveId).Generation)), liveView), "toggle retains selected live browser and all profiles");
                Require(await liveView.CoreWebView2.ExecuteScriptAsync("window.sidebarMarker==='preserved'&&document.title==='A18 fixture'") == "true", "toggle does not reload live page");
            }
            Console.WriteLine("PASS: production profiles sidebar collapses to 40-DIP strip at 900/1280 widths; resized width, keyboard focus, search/group/selection restored; live page and three environments retained without reload.");
            var timings=new List<double>();
            var stages=new List<string>{"action,searchMs,selectionMs,renderMs,totalMs"};
            for(var action=0;action<30;action++) {
                var previousSelection=list.SelectedItem;
                var watch=System.Diagnostics.Stopwatch.StartNew();search.Text=action%2==0?"perf":"";
                var searched=watch.Elapsed.TotalMilliseconds;
                if(previousSelection is ProfileItem previous && liveIds.Contains(previous.Id))
                    Require(ReferenceEquals(previousSelection,list.SelectedItem),"search preserves the selected live row identity");
                list.SelectedItem=list.Items.OfType<ProfileItem>().Single(p=>p.Id==liveIds[action%3]);
                var selected=watch.Elapsed.TotalMilliseconds;
                await shell.Dispatcher.InvokeAsync(shell.UpdateLayout,DispatcherPriority.Render);
                watch.Stop();timings.Add(watch.Elapsed.TotalMilliseconds);
                stages.Add(FormattableString.Invariant($"{action},{searched:F2},{selected-searched:F2},{watch.Elapsed.TotalMilliseconds-selected:F2},{watch.Elapsed.TotalMilliseconds:F2}"));
                var selectedId=liveIds[action%3];
                Require(((IBrowserViewHost)shell).ActiveView(new(selectedId,lifecycle.GetState(selectedId).Generation)) is not null,"selected live browser is available");
            }
            var p95=timings.Order().ElementAt((int)Math.Ceiling(timings.Count*.95)-1);
            Directory.CreateDirectory("artifacts/test-results");File.WriteAllLines("artifacts/test-results/directory-performance.csv",stages);
            Console.WriteLine("Directory performance stages: "+string.Join("; ",stages.Skip(1)));
            Require(p95<200,"100-profile UI search/selection p95 <=200ms: "+p95);
            Console.WriteLine($"PASS: 105-profile production WPF directory with three real open profiles; 30 search/selection actions p95={p95:F2}ms; fourth environment rejected; CPUs={Environment.ProcessorCount}; available RAM={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
            Console.WriteLine("PASS: native modern WPF UI; production theme/main XAML and dialogs; 1280/900 layouts; search/group filter/overflow menus; keyboard focus and validation; custom URL/group creation; five settings categories and unchanged-value save; screenshots captured.");
        }
        finally
        {
            foreach(var id in liveIds)Require((await lifecycle.CloseAsync(id)).Outcome==CloseOutcome.Closed,"UI fixture browser fully exited");
            beforeClosing=new(shell.Left,shell.Top,shell.Width,shell.Height,false);shell.Close();
        }
        var savedPlacement=new WindowPlacementStore(paths).Load() ?? throw new InvalidOperationException("Window placement was not saved.");
        Require(savedPlacement==beforeClosing && repository.Get(original!.Id)!.WindowBounds is not null,"global and per-profile placement save");
        var reopened=new MainWindow(paths,repository,catalog,credentials,permissions,runtimeVersion,updater) { ShowInTaskbar=false };
        reopened.Initialize(engine);
        try { reopened.Show();await Layout(reopened);var area=SystemParameters.WorkArea;var fitted=WindowPlacementStore.Fit(savedPlacement,new(area.Left,area.Top,area.Width,area.Height,false),reopened.MinWidth,reopened.MinHeight);Require(reopened.Width==fitted.Width&&reopened.Height==fitted.Height&&reopened.Left==fitted.Left&&reopened.Top==fitted.Top,"placement restore fits visible desktop"); }
        finally {reopened.Close();}
        Console.WriteLine("PASS: production WPF window geometry saved globally/per-profile and restored on restart; off-screen placement fitted into available work area.");
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
