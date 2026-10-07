using System.Diagnostics;
using System.Windows;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Services;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Storage;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App;

public partial class App : Application
{
    private MailfudGeoIpUpdater? _geoIpUpdater;
    public const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WebView2 Runtime detection is separate from the .NET Desktop Runtime prerequisite (spec §11, A19).
        var runtime = WebView2Engine.TryGetInstalledRuntimeVersion();
        if (runtime is null)
        {
            var answer = MessageBox.Show(
                "Не найдена среда выполнения Microsoft Edge WebView2. Без неё приложение не может открыть почту.\n\n" +
                "Открыть страницу загрузки WebView2 Runtime (Evergreen)? Данные профилей сохранятся.",
                "SecureBrowser", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
            Shutdown(2);
            return;
        }

        var paths = ManagedPaths.ForCurrentUser();
        paths.EnsureBaseDirectories();

        SqliteProfileRepository repository;
        try
        {
            repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        }
        catch (SchemaMigrationException ex)
        {
            // Never rebuild an empty database silently (spec §9, A32).
            MessageBox.Show(
                ex.Message + (ex.BackupPath is null ? string.Empty : $"\n\nРезервная копия: {ex.BackupPath}") +
                "\n\nПриложение будет закрыто без изменения базы данных.",
                "SecureBrowser", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        var credentials = new WindowsCredentialStore();
        var permissions = new PermissionPolicy(repository);
        var navigation = CreateNavigationPolicy();
        var catalog = new ProfileCatalog(repository, credentials);

        _geoIpUpdater = new MailfudGeoIpUpdater(paths);
        var window = new MainWindow(paths, repository, catalog, credentials, permissions, runtime, _geoIpUpdater);
        var engine = new WebView2Engine(window, paths, permissions, navigation, credentials);
        window.Initialize(engine);
        MainWindow = window;
        window.Show();
        _ = _geoIpUpdater.RunAsync();
        await window.ResumeInterruptedOperationsAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _geoIpUpdater?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Production permits HTTP(S) sites with a profile-specific start page. Development builds may use an owned HTTPS fixture
    /// (PP_FIXTURE_ORIGINS="https://localhost:8443;https://127.0.0.1:8444", PP_FIXTURE_START=https://localhost:8443/).
    /// Release builds ignore these variables.
    /// </summary>
    private static NavigationPolicy CreateNavigationPolicy()
    {
#if DEV_BUILD
        var origins = Environment.GetEnvironmentVariable("PP_FIXTURE_ORIGINS");
        var start = Environment.GetEnvironmentVariable("PP_FIXTURE_START");
        if (!string.IsNullOrWhiteSpace(origins) && Uri.TryCreate(start, UriKind.Absolute, out var startUri))
            return new NavigationPolicy(origins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), startUri);
#endif
        return new NavigationPolicy();
    }
}
