using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Downloads;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.App.Browser;

/// <summary>
/// WebView2 adapter: one CoreWebView2Environment and one UserDataFolder per profile, one persistent browser profile
/// per environment (spec §2, §4). All members must be used on the WPF STA UI thread.
/// </summary>
public sealed class WebView2Engine : IBrowserEngine
{
    public const string BrowserProfileName = "Default";

    private readonly IBrowserViewHost _host;
    private readonly ManagedPaths _paths;
    private readonly PermissionPolicy _permissions;
    private readonly NavigationPolicy _navigation;
    private readonly ICredentialStore _credentials;
    internal Action<string>? PrivacyDiagnostic { get; set; }

    public WebView2Engine(IBrowserViewHost host, ManagedPaths paths, PermissionPolicy permissions, NavigationPolicy navigation, ICredentialStore credentials)
    {
        _host = host;
        _paths = paths;
        _permissions = permissions;
        _navigation = navigation;
        _credentials = credentials;
    }

    public BrowserCapabilities Capabilities { get; } = new(
        EngineName: "WebView2",
        PersistentStorage: true,
#if EXPERIMENTAL_PROXY
        ProxySupport: ProxySupportLevel.ExperimentalBrowserFlag,
#else
        ProxySupport: ProxySupportLevel.None,
#endif
        ProxyChangeRequiresRestart: true,
        UserAgentChangeRequiresRestart: true,
        LanguageChangeRequiresRestart: true,
        ScriptLocaleSupported: true,
        ColorSchemeLive: true,
        ZoomLive: true)
    {
        GraphicsRestrictionSupported = true,
#if EXPERIMENTAL_PROXY
        WebRtcNetworkRestrictionSupported = true,
#endif
    };

    /// <summary>Runtime detection for startup and diagnostics (spec §2, A19).</summary>
    public static string? TryGetInstalledRuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return null; }
    }

    public static string SdkVersion => typeof(CoreWebView2Environment).Assembly.GetName().Version?.ToString() ?? "unknown";

    public async Task<IBrowserSession> StartAsync(BrowserStartRequest request, CancellationToken cancellationToken)
    {
        var config = request.Config;
        var context = request.Context;
        var errors = ProfileValidator.Validate(config);
        if (errors.Count > 0) throw new BrowserStartException(string.Join(" ", errors), processMayExist: false);
        if (config.BrowserTimeZoneAuto)
        {
            try { _ = new GeoIpTimeZoneDatabase(_paths).Inspect(); }
            catch (Exception e) { throw new BrowserStartException(e.Message, processMayExist: false, inner: e); }
        }
        NavigationPolicy navigation;
        try { navigation = _navigation.ForProfile(config); }
        catch (ArgumentException e) { throw new BrowserStartException(e.Message, processMayExist: false, inner: e); }

        if (!Enum.IsDefined(config.WebRtcPagePolicy) || !Enum.IsDefined(config.WebRtcNetworkPolicy))
            throw new BrowserStartException("Неизвестная политика WebRTC.", processMayExist: false);
        if (config.WebRtcNetworkPolicy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental)
        {
            if (!Capabilities.WebRtcNetworkRestrictionSupported)
                throw new BrowserStartException("Ограничение сети WebRTC недоступно в этой сборке.", processMayExist: false);
        }

        if ((config.NetworkMode == NetworkMode.Proxy
             || config.WebRtcNetworkPolicy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental
             || config.GraphicsPolicy != GraphicsPolicy.RuntimeDefault)
            && HasBrowserArgumentOverrides())
            throw new BrowserStartException("Внешние настройки аргументов браузера могут изменить прокси, WebRTC или ограничение графики. Открытие заблокировано.", processMayExist: false);

        // Step 2: environment options must be complete before any controller exists.
        var options = new CoreWebView2EnvironmentOptions
        {
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            AreBrowserExtensionsEnabled = false,
            ExclusiveUserDataFolderAccess = true,
            EnableTrackingPrevention = true,
            Language = config.LanguageMode == LanguageMode.Custom ? config.LanguageTag! : string.Empty,
        };

        switch (config.NetworkMode)
        {
            case NetworkMode.System:
                break;
            case NetworkMode.Proxy:
#if EXPERIMENTAL_PROXY
                break;
#else
                // An unsupported Proxy configuration stays blocked; never open as System (spec §2).
                throw new BrowserStartException("Режим прокси недоступен в основной сборке.", processMayExist: false);
#endif
            default:
                throw new BrowserStartException("Сетевой режим не выбран.", processMayExist: false);
        }
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        AuthenticatedProxyRelay? relay = null;
        if (config.NetworkMode == NetworkMode.Proxy && config.Proxy is { AuthMode: ProxyAuthMode.Basic, CredentialRef: not null } authenticated)
        {
            var credential = _credentials.Read(authenticated.CredentialRef)
                ?? throw new BrowserStartException("Учётные данные прокси не найдены.", processMayExist: false);
            relay = new AuthenticatedProxyRelay(authenticated.Endpoint!, credential, message =>
                dispatcher.BeginInvoke(new Action(() => { if (request.IsCurrentGeneration(context)) _host.ReportProblem(context, message); })));
        }
        // Assign the complete validated string once: an RTC flag must not replace the proxy flag.
        options.AdditionalBrowserArguments = BrowserArguments.Build(
            config.NetworkMode == NetworkMode.Proxy ? relay?.Endpoint ?? config.Proxy!.Endpoint! : null, config.WebRtcNetworkPolicy, config.GraphicsPolicy, config.PrivacyExceptions);

        CoreWebView2Environment environment;
        try
        {
            WebViewDefaultProfileMigration.Prepare(request.UserDataFolder);
            environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: request.UserDataFolder, options: options);
        }
        catch (WebView2RuntimeNotFoundException e)
        {
            relay?.Dispose();
            throw new BrowserStartException("Среда выполнения WebView2 не установлена.", processMayExist: false, inner: e);
        }
        catch (Exception e)
        {
            relay?.Dispose();
            throw new BrowserStartException("Не удалось создать среду браузера: " + e.Message, processMayExist: false, inner: e);
        }

        // Subscribe before anything can shut the environment down (spec §4.4).
        var tabsStore = new BrowserTabsStore(_paths);
        var savedTabs = tabsStore.Load(context.ProfileId);
        var session = new WebView2Session(context, environment, _host) { Request = request, Config = config, TabsStore = tabsStore, ProxyRelay = relay };

        session.CreateEmptyTabAsync = () => OpenTabAsync(session);

        // Step 3: the effective UDF and channel must match; policies or env vars can override supplied values (S23).
        if (!_paths.IsExpectedUserDataFolder(context.ProfileId, environment.UserDataFolder))
        {
            relay?.Dispose();
            throw new BrowserStartException("Фактическая папка данных браузера не совпадает с ожидаемой; открытие заблокировано.", processMayExist: false);
        }
        if (!IsStableChannel(environment.BrowserVersionString))
        {
            relay?.Dispose();
            throw new BrowserStartException($"Неожиданный канал среды выполнения ({environment.BrowserVersionString}); открытие заблокировано.", processMayExist: false);
        }

        if (cancellationToken.IsCancellationRequested) { relay?.Dispose(); cancellationToken.ThrowIfCancellationRequested(); } // still no browser process

        // Step 4: persistent profile and script locale on the controller options.
        var controllerOptions = CreateControllerOptions(environment, config);
        session.ControllerOptions = controllerOptions;

        var view = new WebView2 { Visibility = Visibility.Hidden };
        _host.Attach(context, view);
        session.SetMainView(view);
        try
        {
            await view.EnsureCoreWebView2Async(environment, controllerOptions);
        }
        catch (Exception e)
        {
            // A browser process may already exist; the caller enters RecoveryRequired and awaits this session's exit.
            await session.CloseAsync();
            throw new BrowserStartException("Не удалось инициализировать WebView2: " + e.Message, processMayExist: true, partialSession: session, inner: e);
        }

        // Step 5: handlers and settings before the first explicit navigation.
        var core = view.CoreWebView2;
        session.BrowserProcessId = (int)core.BrowserProcessId;
        session.RuntimeVersion = environment.BrowserVersionString;
        try
        {
            ProfileLock.RecordBrowserProcess(request, session.BrowserProcessId.Value);
            if (cancellationToken.IsCancellationRequested) return session; // caller disposes it; never navigates
            BrowserWindowCloseHandling.UseTabOwnership(view);
            session.RegisterController(view);
            view.ZoomFactor = config.ZoomFactor;
            ConfigureProfile(core.Profile, config);
            var authenticationConfig = config;
            await ConfigureAuthenticationAsync(core, session, request, authenticationConfig);
            if (config.BrowserTimeZoneAuto)
            {
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                if (config.UserAgentMode == UserAgentMode.Custom) core.Settings.UserAgent = config.CustomUserAgent!;
                await ClientHintsRequests.ForCore(core, () => !session.IsClosing && request.IsCurrentGeneration(context),
                    reason => StopAfterPrivacyFailureAsync(session, request, reason), stripClientHints: UserAgentHintsPrivacy.IsEnabled(config.GraphicsPolicy)).ConfigureAsync();
                var addresses = await AutoTimeZoneBootstrap.DiscoverAsync(core, cancellationToken);
                if (!request.IsCurrentGeneration(context) || cancellationToken.IsCancellationRequested) return session;
                session.AutoTimeZone = new GeoIpTimeZoneDatabase(_paths).Resolve(addresses);
                config = config with { BrowserTimeZoneAuto = false, BrowserTimeZoneId = session.AutoTimeZone.TimeZoneId };
                session.Config = config;
                if (!StandardFingerprintPrivacy.IsEnabled(config.GraphicsPolicy) || ProfilePrivacy.Allows(config, PrivacyException.ServiceWorkers))
                    await core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker", "{\"bypass\":false}");
            }
            await ConfigureWebViewAsync(session, core, config, request, authenticationConfigured: true);
        }
        catch (Exception e)
        {
            await session.CloseAsync();
            throw new BrowserStartException("Не удалось применить защиту страницы; открытие заблокировано: " + e.Message,
                processMayExist: true, partialSession: session, inner: e);
        }
        view.ZoomFactor = config.ZoomFactor;

        // Local connection log for the user (never part of the shareable diagnostics report). Enabled before the first
        // navigation so the trace starts with the first request; a failure here must not block opening.
        session.LogFile = ConnectionLogFile.TryStart(_paths, context.ProfileId, session.Connections, DateTimeOffset.Now);
        await AttachNetworkLogAsync(core, session, "основное окно");
        if (!request.IsCurrentGeneration(context) || cancellationToken.IsCancellationRequested) return session;

        // Step 6: explicit navigation.
        session.TabReady(view);
        var firstAddress = savedTabs is { Addresses.Length: > 0 } ? savedTabs.Addresses[0] : navigation.StartUri.AbsoluteUri;
        if (config.NetworkMode == NetworkMode.Proxy)
        {
            try
            {
                var checkAddress = firstAddress == "about:blank" ? navigation.StartUri.AbsoluteUri : firstAddress;
                await ProxyStartupCheck.NavigateAsync(core, checkAddress, cancellationToken,
                    !StandardFingerprintPrivacy.IsEnabled(config.GraphicsPolicy) || ProfilePrivacy.Allows(config, PrivacyException.ServiceWorkers));
                if (!request.IsCurrentGeneration(context) || cancellationToken.IsCancellationRequested) return session;
                if (checkAddress != firstAddress) core.Navigate(firstAddress);
            }
            catch (Exception e)
            {
                await session.CloseAsync();
                throw new BrowserStartException("Подключение через прокси не подтверждено; настройки не применены. " + e.Message,
                    processMayExist: true, partialSession: session, inner: e);
            }
        }
        else core.Navigate(firstAddress);
        if (savedTabs is { Addresses.Length: > 0 })
        {
            try
            {
                for (var index = 1; index < savedTabs.Addresses.Length; index++)
                {
                    if (cancellationToken.IsCancellationRequested || !request.IsCurrentGeneration(context)) return session;
                    var restored = await PrepareTabAsync(session);
                    if (restored is null) return session;
                    session.TabReady(restored);
                    restored.CoreWebView2.Navigate(savedTabs.Addresses[index]);
                }
                if (session.IsClosing || cancellationToken.IsCancellationRequested || !request.IsCurrentGeneration(context)) return session;
                _host.SelectTab(context, session.Views[savedTabs.ActiveIndex]);
            }
            catch (Exception e)
            {
                await session.CloseAsync();
                throw new BrowserStartException("Не удалось восстановить вкладки; сохранённый список не изменён: " + e.Message,
                    processMayExist: true, partialSession: session, inner: e);
            }
        }
        session.StartupCompleted = true;
        return session;
    }

    /// <summary>Creates another controller in the existing profile; applies the same guards before navigation.</summary>
    public async Task<WebView2?> OpenTabAsync(WebView2Session session, string address = "about:blank")
    {
        if (!BrowserAddress.TryNormalize(address, out var normalized))
            throw new ArgumentException("Введите HTTP/HTTPS адрес сайта.", nameof(address));
        try
        {
            var view = await PrepareTabAsync(session);
            if (view is null) return null;
            session.TabReady(view);
            view.CoreWebView2.Navigate(normalized);
            return view;
        }
        catch (Exception e)
        {
            if (session.IsClosing || session.Request is not { } request || !request.IsCurrentGeneration(session.Context)) return null;
            _host.ReportProblem(session.Context, "Не удалось открыть вкладку: " + e.Message);
            await StopAfterPrivacyFailureAsync(session, request, "Не удалось подготовить защиту новой вкладки.");
            return null;
        }
    }

    private async Task<WebView2?> PrepareTabAsync(WebView2Session session, bool preserveUnnavigated = false)
    {
        if (session.IsClosing || session.ControllerOptions is not { } options || session.Config is not { } config
            || session.Request is not { } request || !request.IsCurrentGeneration(session.Context)) return null;
        var zoom = session.MainView?.ZoomFactor ?? config.ZoomFactor;
        config = config with { ZoomFactor = zoom };
        var view = await session.CreateTabViewAsync(options);
        if (view is null) return null;
        if (session.IsClosing || !request.IsCurrentGeneration(session.Context)) { session.RemoveView(view); return null; }
        view.ZoomFactor = zoom;
        ConfigureProfile(view.CoreWebView2.Profile, config);
        await ConfigureWebViewAsync(session, view.CoreWebView2, config, request, childWindow: preserveUnnavigated);
        view.ZoomFactor = zoom;
        await AttachNetworkLogAsync(view.CoreWebView2, session, "вкладка");
        if (session.IsClosing || !request.IsCurrentGeneration(session.Context)) { session.RemoveView(view); return null; }
        return view;
    }

    internal static bool IsStableChannel(string? version) =>
        !string.IsNullOrEmpty(version) && !version.Contains(' ', StringComparison.Ordinal);

    private static bool HasBrowserArgumentOverrides()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"))) return true;
        // Conservative: any deployed argument policy (including AppUserModelID entries) needs separate verification.
        // Do not expose registry values in diagnostics or attempt to change organizational policy.
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments");
                if (key?.GetValueNames().Length > 0) return true;
            }
            return false;
        }
        catch (Exception) { return true; } // unknown policy cannot establish an uncontested experiment
    }

    private static CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment environment, ProfileConfig config)
    {
        var o = environment.CreateCoreWebView2ControllerOptions();
        // The runtime default is the persistent "Default" profile. Explicitly assigning the same name can
        // create a separate named BrowserContext that older WebView2 CDP cannot address with Browser.setPermission.
        o.IsInPrivateModeEnabled = false;
        var locale = config.ResolveScriptLocale(CultureInfo.CurrentUICulture.Name);
        if (!string.IsNullOrEmpty(locale)) o.ScriptLocale = locale;
        return o;
    }

    private static void ConfigureProfile(CoreWebView2Profile profile, ProfileConfig config)
    {
        profile.PreferredColorScheme = ToApi(config.ColorScheme);
        profile.PreferredTrackingPreventionLevel = config.TrackingPreventionLevel == TrackingPreventionLevel.Strict
            ? CoreWebView2TrackingPreventionLevel.Strict
            : CoreWebView2TrackingPreventionLevel.Balanced;
        // Spec §7: no extra browser credential/form store in the MVP. Proton session data is unaffected.
        profile.IsPasswordAutosaveEnabled = false;
        profile.IsGeneralAutofillEnabled = false;
    }

    /// <summary>UI "Системная" → Auto (S19).</summary>
    public static CoreWebView2PreferredColorScheme ToApi(ColorSchemePreference scheme) => scheme switch
    {
        ColorSchemePreference.Light => CoreWebView2PreferredColorScheme.Light,
        ColorSchemePreference.Dark => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    /// <summary>Applies settings and handlers shared by the main view and every child window of a generation.</summary>
    private async Task ConfigureWebViewAsync(WebView2Session session, CoreWebView2 core, ProfileConfig config, BrowserStartRequest request, bool childWindow = false, bool authenticationConfigured = false)
    {
        var ctx = request.Context;
        bool IsCurrentView() => IsViewCurrent(session, request, core);
        Task PrivacyFailure(string reason) => IsCurrentView() ? StopAfterPrivacyFailureAsync(session, request, reason) : Task.CompletedTask;
        var navigation = _navigation.ForProfile(config);
        if (!authenticationConfigured) await ConfigureAuthenticationAsync(core, session, request, config);
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
#if DEV_BUILD
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsStatusBarEnabled = true;
        await BrowserPermissionRequests.InstallAsync(core, config, IsCurrentView,
            e => HandlePermission(ctx, request, config, e), diagnostic: PrivacyDiagnostic);
        await session.InitializePermissionGuardAsync(config, PrivacyDiagnostic);
        await UserAgentHintsBootstrap.ApplyAsync(core, config, IsCurrentView, PrivacyFailure, diagnostic: PrivacyDiagnostic, applyHardwarePermissions: false);
        await UserAgentHintsBootstrap.VerifyAsync(core, session.Environment, config, verify: !childWindow, diagnostic: PrivacyDiagnostic,
            refreshHardwarePermissions: () => session.RefreshHardwarePermissionsAsync(config, PrivacyDiagnostic));
        // Apply the internal-origin guard in every real profile controller, including
        // popups and non-strict modes, before its first website navigation.
        await ClientHintsRequests.ForCore(core, IsCurrentView, PrivacyFailure, stripClientHints: false).ConfigureAsync();

        core.NavigationStarting += (_, e) =>
        {
            if (!IsCurrentView()) { e.Cancel = true; return; }
            var decision = navigation.EvaluateTopLevel(e.Uri);
            if (decision == TopLevelDecision.Allow) return;
            e.Cancel = true;
            // Never auto-launch; offer an explicit action and never for service URLs (tokens).
            if (decision == TopLevelDecision.BlockOfferExternal && e.IsUserInitiated && !navigation.IsServiceUrl(e.Uri))
                _host.OfferExternalLink(ctx, e.Uri);
        };

        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            var failed = false;
            try
            {
                e.Handled = true;
                if (!IsCurrentView()) return;
                var target = e.Uri;
                var allowed = navigation.EvaluateTopLevel(target) == TopLevelDecision.Allow
                              || (target.StartsWith("blob:", StringComparison.Ordinal) && navigation.EvaluateTopLevel(target[5..]) == TopLevelDecision.Allow);
                if (!allowed)
                {
                    if (NavigationPolicy.IsExternalLaunchable(target) && !navigation.IsServiceUrl(target)) _host.OfferExternalLink(ctx, target);
                    return; // cancelled: Handled without NewWindow
                }
                // Same environment and profile, unnavigated child (S18).
                var child = await PrepareTabAsync(session, preserveUnnavigated: true);
                if (child is null) return;
                if (!IsCurrentView()) { session.RemoveView(child); return; }
                e.NewWindow = child.CoreWebView2;
                session.TabReady(child);
            }
            catch (Exception ex)
            {
                if (IsCurrentView())
                {
                    _host.ReportProblem(ctx, "Не удалось открыть вкладку: " + ex.Message);
                    failed = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
            if (failed) await StopAfterPrivacyFailureAsync(session, request, "Не удалось подготовить защиту дочернего окна.");
        };

        core.WindowCloseRequested += async (_, _) =>
        {
            // WPF may clear CoreWebView2 before forwarding native window.close. Retain the controller mapping.
            if (session.FindView(core) is { } closing)
                await session.CloseTabAsync(closing);
        };


        await session.InitializeDownloadsAsync(core);
        core.DownloadStarting += (_, e) =>
        {
            if (session.FindView(core) is { } owner) HandleDownload(session, owner, request, config, e);
            else e.Cancel = true;
        };

        core.ProcessFailed += async (_, e) =>
        {
            if (!IsCurrentView()) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                // Release dead controllers so the environment can signal resource exit.
                // The lifecycle still retains its lock until BrowserProcessExited arrives.
                try { await session.CloseAsync(); }
                catch (Exception error)
                {
                    PrivacyDiagnostic?.Invoke("Browser crash controller cleanup failed: " + error);
                    _host.ReportProblem(ctx, "Не удалось закрыть контроллеры после сбоя браузера: " + error.Message);
                }
                return;
            }
            _host.ReportProblem(ctx, $"Сбой процесса браузера: {e.ProcessFailedKind}.");
        };
        await VerifyDisplayScaleAsync(core, config, verify: !childWindow);
        await ApplyBrowserTimeZoneAsync(core, config, verify: !childWindow);
        await InstallPageGuardAsync(core, session, config, request, preserveUnnavigated: childWindow,
            scope: authenticationConfigured ? WebRtcReadbackScope.MainDocument : WebRtcReadbackScope.ChildDocument);
        if (!childWindow) await VerifyGraphicsRestrictionAsync(core, config);
    }

    private static bool IsViewCurrent(WebView2Session session, BrowserStartRequest request, CoreWebView2 core) =>
        !session.IsClosing && request.IsCurrentGeneration(request.Context)
        && session.FindView(core) is { } view && session.ContainsView(view);

    private static async Task VerifyGraphicsRestrictionAsync(CoreWebView2 core, ProfileConfig config)
    {
        if (!ProfilePrivacy.BlockGraphics(config) && !ProfilePrivacy.BlockCanvas(config) && !SpeechPrivacy.IsEnabled(config)) return;
        await NavigateToOwnedBlankAsync(core);
        if(ProfilePrivacy.BlockGraphics(config)) {
        var result = GraphicsRestriction.ReadWebGlResult(await core.ExecuteScriptAsync(GraphicsRestriction.WebGlVerificationScript));
        if (result.Outcome != GraphicsReadbackOutcome.Verified)
            throw new InvalidOperationException("Ограничение WebGL не подтверждено; открытие заблокировано. " + result.Detail);
        }
        if (ProfilePrivacy.BlockCanvas(config))
        {
            var canvasResult = CanvasReadback.ReadCdpResult(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = CanvasReadback.EvaluationScript, awaitPromise = true, returnByValue = true })));
            if (canvasResult.Outcome != GraphicsReadbackOutcome.Verified)
                throw new InvalidOperationException("Ограничение чтения Canvas не подтверждено; открытие заблокировано. " + canvasResult.Detail);
        }
        if (SpeechPrivacy.IsEnabled(config))
        {
            var speechResult = SpeechPrivacy.ReadResult(await core.ExecuteScriptAsync(SpeechPrivacy.EvaluationScript));
            if (speechResult.Outcome != GraphicsReadbackOutcome.Verified)
                throw new InvalidOperationException("Ограничение синтеза речи не подтверждено; открытие заблокировано. " + speechResult.Detail);
        }
        // about:blank is not a reliable secure-context WebGPU test. The HTTPS probe reports adapters separately.
    }

    private static async Task NavigateToOwnedBlankAsync(CoreWebView2 core)
    {
        var blankReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBlankReady(object? sender, CoreWebView2NavigationCompletedEventArgs e) => blankReady.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += OnBlankReady;
        try
        {
            core.Navigate("about:blank");
            if (!await blankReady.Task.WaitAsync(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("Не удалось подготовить начальный документ.");
        }
        finally { core.NavigationCompleted -= OnBlankReady; }
    }

    private static async Task VerifyDisplayScaleAsync(CoreWebView2 core, ProfileConfig config, bool verify, double? expectedZoom = null)
    {
        if (!ScreenPrivacy.IsEnabled(config.GraphicsPolicy)) return;
        // Children share the environment flag and must remain unnavigated. Main/probe bootstrap checks the native result.
        if (!verify) return;
        await NavigateToOwnedBlankAsync(core);
        var result = ScreenPrivacy.ReadResult(await core.ExecuteScriptAsync(ScreenPrivacy.EvaluationScript), expectedZoom ?? config.ZoomFactor);
        if (result.Outcome != GraphicsReadbackOutcome.Verified)
            throw new InvalidOperationException("Нормализация DPR не подтверждена; открытие заблокировано. " + result.Detail);
    }

    private static async Task ApplyBrowserTimeZoneAsync(CoreWebView2 core, ProfileConfig config, bool verify)
    {
        if (config.BrowserTimeZoneId is not { } zone) return;
        // Native emulation before navigation; no page API wrappers or fixed UTC offsets.
        // An unsupported Runtime must not silently continue with a different requested zone.
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTimezoneOverride",
                JsonSerializer.Serialize(new { timezoneId = zone }));
            // Child views must stay unnavigated until assigned to NewWindowRequested.
            if (verify)
            {
                await NavigateToOwnedBlankAsync(core);
                if (await core.ExecuteScriptAsync(BrowserTimeZone.VerificationScript(zone)) != "true")
                    throw new InvalidOperationException("Среда выполнения не подтвердила часовой пояс и сезонные смещения.");
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("Не удалось применить часовой пояс браузера. Проверьте настройку и WebView2 Runtime.", e);
        }
    }

    /// <summary>Used by every profile controller, including the isolated bundled diagnostic page.</summary>
    private async Task InstallPageGuardAsync(CoreWebView2 core, WebView2Session session, ProfileConfig config, BrowserStartRequest request,
        bool preserveUnnavigated = false, WebRtcReadbackScope scope = WebRtcReadbackScope.MainDocument)
    {
        var rtc = config.WebRtcPagePolicy == WebRtcPagePolicy.Block;
        var audio = AudioPageGuard.IsEnabled(config);
        if (!rtc && !audio) return;
        if (rtc)
        {
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcPageGuard.Script); session.WebRtcGuardRegistrations++; }
            catch { session.WebRtcGuardFailed = true; throw; }
        }
        if (audio)
        {
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.Script); session.AudioGuardRegistrations++; }
            catch { session.AudioGuardFailed = true; throw; }
        }
        // NewWindow requires an unnavigated controller. For children, await registration before assigning it;
        // its first document is observed below. Never bootstrap-navigate a pending NewWindow controller.
        if (!preserveUnnavigated)
        {
            // Registration applies only to future documents. Bootstrap an owned blank document, with no target scripts,
            // and verify actual injection before any target navigation (also for child/persisted-profile controllers).
            await NavigateToOwnedBlankAsync(core);
            if (session.IsClosing || !request.IsCurrentGeneration(request.Context))
                throw new InvalidOperationException("Профиль закрывается.");
            if (rtc && await core.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript) != "true")
            {
                session.WebRtcGuardFailed = true;
                throw new InvalidOperationException("Блокировка WebRTC в начальном документе не подтверждена.");
            }
            if (audio && await core.ExecuteScriptAsync(AudioPageGuard.VerifyScript) != "true")
            {
                session.AudioGuardFailed = true;
                throw new InvalidOperationException("Блокировка Web Audio в начальном документе не подтверждена.");
            }
        }

        // These late observations detect failures; they cannot retroactively prevent earlier traffic.
        var document = new PageDocumentTracker();
        core.NavigationStarting += (_, e) => document.NavigationStarting(e.NavigationId);
        core.DOMContentLoaded += async (_, e) =>
        {
            if (document.DocumentReady(e.NavigationId) is { } isCurrentDocument)
                await ObserveDocumentGuardsAsync(core.ExecuteScriptAsync, session, request,
                    () => IsViewCurrent(session, request, core) && isCurrentDocument(), scope, rtc, audio);
        };
        core.FrameCreated += (_, e) =>
        {
            var frame = e.Frame;
            var frameDocument = new PageDocumentTracker();
            frame.NavigationStarting += (_, navigation) => frameDocument.NavigationStarting(navigation.NavigationId);
            frame.Destroyed += (_, _) => frameDocument.Destroy();
            frame.DOMContentLoaded += async (_, ready) =>
            {
                if (frameDocument.DocumentReady(ready.NavigationId) is { } isCurrentDocument)
                    await ObserveDocumentGuardsAsync(frame.ExecuteScriptAsync, session, request,
                        () => IsViewCurrent(session, request, core) && isCurrentDocument(), WebRtcReadbackScope.Frame, rtc, audio);
            };
        };
    }

    private async Task ObserveDocumentGuardsAsync(Func<string, Task<string>> read, WebView2Session session, BrowserStartRequest request,
        Func<bool> isCurrentDocument, WebRtcReadbackScope scope, bool rtc, bool audio)
    {
        if (rtc) await ObserveGuardAsync(() => read(WebRtcPageGuard.VerifyScript), session, request, isCurrentDocument, scope);
        if (!audio || session.IsClosing) return;
        var outcome = await PageGuardReadback.ObserveAsync(() => read(AudioPageGuard.VerifyScript),
            () => !session.IsClosing && request.IsCurrentGeneration(request.Context) && isCurrentDocument());
        session.AudioReadback = session.AudioReadback.Record(outcome, scope.ToString());
        if (outcome == PageGuardReadbackOutcome.Unavailable && session.AudioReadback.Unavailable == 1)
            _host.ReportProblem(request.Context, "Не удалось проверить блокировку Web Audio в одном из документов. Полное покрытие не подтверждено.");
        if (outcome == PageGuardReadbackOutcome.Violation)
        {
            session.AudioGuardFailed = true;
            await StopAfterPrivacyFailureAsync(session, request, "Подтверждено нарушение блокировки Web Audio в документе.");
        }
    }

    private async Task ObserveGuardAsync(Func<Task<string>> observe, WebView2Session session, BrowserStartRequest request,
        Func<bool> isCurrentDocument, WebRtcReadbackScope scope)
    {
        var outcome = await WebRtcGuardReadback.ObserveAsync(observe,
            () => !session.IsClosing && request.IsCurrentGeneration(request.Context) && isCurrentDocument());
        session.WebRtcReadback = session.WebRtcReadback.Record(outcome, scope);
        if (outcome == WebRtcReadbackOutcome.Unavailable && session.WebRtcReadback.Unavailable == 1)
        {
            _host.ReportProblem(request.Context, "Не удалось проверить блокировку WebRTC в одном из документов. Блокировка остаётся включённой; полное покрытие не подтверждено.");
        }
        if (outcome == WebRtcReadbackOutcome.Violation)
            await FailWebRtcProfileAsync(session, request,
                scope == WebRtcReadbackScope.Frame ? "Подтверждено нарушение блокировки WebRTC во вложенном документе."
                    : "Подтверждено нарушение блокировки WebRTC в документе окна.");
    }

    private async Task FailWebRtcProfileAsync(WebView2Session session, BrowserStartRequest request,
        string reason = "Не удалось применить блокировку WebRTC к новому окну.")
    {
        if (session.IsClosing) return;
        session.WebRtcGuardFailed = true;
        await StopAfterPrivacyFailureAsync(session, request, reason);
    }

    private async Task StopAfterPrivacyFailureAsync(WebView2Session session, BrowserStartRequest request, string reason)
    {
        if (session.IsClosing || !request.IsCurrentGeneration(request.Context)) return;
        // Stop new contexts immediately, then let the lifecycle gate await this generation's process exit.
        await session.CloseAsync();
        await _host.StopProfileAsync(request.Context, reason + " Открытие заблокировано; проверьте совместимость среды выполнения.");
    }

    /// <summary>
    /// Subscribes to the DevTools Network domain of one view and feeds the session's connection log. This reads
    /// metadata only (URL, status, remote address, protocol, TLS); request and response bodies are never fetched.
    /// </summary>
    private static async Task AttachNetworkLogAsync(CoreWebView2 core, WebView2Session session, string source)
    {
        try
        {
            var parser = new CdpNetworkParser(session.Connections.Add, source);
            foreach (var name in CdpNetworkParser.Events)
            {
                var eventName = name;
                core.GetDevToolsProtocolEventReceiver(eventName).DevToolsProtocolEventReceived += (_, e) =>
                {
                    if (!session.IsClosing) parser.Handle(eventName, e.ParameterObjectAsJson);
                };
            }
            // Small buffers: bodies are not needed, only metadata.
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{\"maxTotalBufferSize\":1048576,\"maxResourceBufferSize\":65536}");
        }
        catch (Exception)
        {
            // Logging is best effort and must never affect the mail session.
        }
    }

    public const string ProbeHost = FingerprintProbePage.Host;
    public static string ProbeUri => FingerprintProbePage.NavigationUri;

    /// <summary>
    /// Initializes <paramref name="view"/> as an extra controller of the profile's own environment and browser profile and
    /// loads the bundled IP/fingerprint page, so it sees exactly what sites see in this profile (same UA, language,
    /// storage and network path). Results arrive through <paramref name="onReport"/> as JSON.
    /// </summary>
    public async Task<string?> InitializeProbeViewAsync(WebView2Session session, WebView2 view, Action<string> onReport)
    {
        if (session.IsClosing || session.ControllerOptions is null || session.Request is null || session.Config is null)
            return "Профиль закрывается.";
        var request = session.Request;
        var config = session.Config;
        var ctx = session.Context;
        try
        {
            await view.EnsureCoreWebView2Async(session.Environment, session.ControllerOptions);
        }
        catch (Exception e)
        {
            return "Не удалось подготовить проверку: " + e.Message;
        }
        if (session.IsClosing || !request.IsCurrentGeneration(ctx)) return "Профиль закрывается.";

        var core = view.CoreWebView2;
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true; // only this bundled page; Proton views keep it disabled
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = true;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        // Mirror the profile before the private diagnostic URL.
        try
        {
            await ConfigureAuthenticationAsync(core, session, request, config);
            await BrowserPermissionRequests.InstallAsync(core, config, () => !session.IsClosing && request.IsCurrentGeneration(ctx),
                denyAll: true, diagnostic: PrivacyDiagnostic);
            await UserAgentHintsBootstrap.ApplyAsync(core, config, () => !session.IsClosing && request.IsCurrentGeneration(ctx),
                reason => StopAfterPrivacyFailureAsync(session, request, reason), applyHardwarePermissions: false);
            await UserAgentHintsBootstrap.VerifyAsync(core, session.Environment, config, verify: true,
                refreshHardwarePermissions: () => session.RefreshHardwarePermissionsAsync(config, PrivacyDiagnostic));
        }
        catch (Exception e) { return e.Message; }
        await FingerprintProbePage.ConfigureAsync(core, session.Environment);
        var probeZoom = session.MainView?.ZoomFactor ?? config.ZoomFactor;
        view.ZoomFactor = probeZoom;
        object activePageCompatibility=new {status="NotPerformed"};
        if(session.MainView?.CoreWebView2 is { } active) {
            try {
                var evaluated=await active.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression=FingerprintProbePage.WebCryptoEvaluationScript,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(5));
                using var document=JsonDocument.Parse(evaluated);
                if(!document.RootElement.TryGetProperty("exceptionDetails",out _))activePageCompatibility=document.RootElement.GetProperty("result").GetProperty("value").Clone();
            } catch { /* Diagnostics remain available when a site's context is gone or unavailable. */ }
        }
        var probeSettings = JsonSerializer.Serialize(new { applicationVersion = FingerprintProbePage.ApplicationVersion, collectorHash = FingerprintProbePage.CollectorHash,
            activePageCompatibility, zoomFactor = probeZoom, profileKind = config.Kind.ToString(),
            graphicsPolicy = config.GraphicsPolicy.ToString(), privacyExceptions = ProfilePrivacy.Names(config.PrivacyExceptions), browserTimeZoneId = config.BrowserTimeZoneId,
            browserTimeZoneAuto = session.AutoTimeZone is not null, geoIpDatabase = session.AutoTimeZone?.Database,
            expectedHardwareConcurrency = UserAgentHintsBootstrap.ExpectedCpu(core),
            expectedUserAgent = UserAgentHintsPrivacy.IsEnabled(config.GraphicsPolicy) ? s.UserAgent : null });
        await core.AddScriptToExecuteOnDocumentCreatedAsync("globalThis.__ppProbeSettings = " + probeSettings + ";");
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CanvasReadback.Script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScreenPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(SpeechPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(UserAgentHintsPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(FontAccessPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HardwareConcurrencyPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HardwareDevicesPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ComputePressurePrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AdditionalFingerprintPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(StandardFingerprintPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ResidualFingerprintPrivacy.ObservationScript);

        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank") return; // owned guard bootstrap only; no report bridge accepts this origin
            if (!FingerprintProbePage.IsPageUri(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += (_, e) =>
        {
            if (!FingerprintProbePage.IsPageUri(e.Source)) return;
            try { onReport(e.TryGetWebMessageAsString()); } catch (ArgumentException) { }
        };
        await AttachNetworkLogAsync(core, session, "проверка отпечатка");
        try { await VerifyDisplayScaleAsync(core, config, verify: true, expectedZoom: probeZoom); }
        catch (Exception e) { return e.Message; }
        try { await ApplyBrowserTimeZoneAsync(core, config, verify: true); }
        catch (Exception) { return "Не удалось применить часовой пояс к странице проверки."; }
        try { await InstallPageGuardAsync(core, session, config, request, scope: WebRtcReadbackScope.DiagnosticDocument); }
        catch (Exception e)
        {
            await StopAfterPrivacyFailureAsync(session, request, "Не удалось применить защиту к странице проверки.");
            return e.Message;
        }
        try { await VerifyGraphicsRestrictionAsync(core, config); }
        catch (Exception e) { return e.Message; }
        if (session.IsClosing || !request.IsCurrentGeneration(ctx)) return "Профиль закрывается.";
        core.Navigate(ProbeUri);
        return null;
    }

    private async void HandlePermission(GenerationContext ctx, BrowserStartRequest request, ProfileConfig config, CoreWebView2PermissionRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            // One authoritative store: the browser must not persist its own decision (S20).
            e.SavesInProfile = false;
            e.Handled = true;
            if (!request.IsCurrentGeneration(ctx)) { e.State = CoreWebView2PermissionState.Deny; return; }
            var allowed = !AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy) || Map(e.PermissionKind) is PermissionKindKey.Notifications or PermissionKindKey.ClipboardRead
                || Map(e.PermissionKind)==PermissionKindKey.Camera&&ProfilePrivacy.Allows(config,PrivacyException.Camera)
                || Map(e.PermissionKind)==PermissionKindKey.Microphone&&ProfilePrivacy.Allows(config,PrivacyException.Microphone)
                ? await _permissions.ResolveAsync(ctx, e.Uri, Map(e.PermissionKind), (origin, kind) => _host.AskPermissionAsync(ctx, origin, kind), config.WebRtcPagePolicy) : false;
            e.State = allowed && request.IsCurrentGeneration(ctx) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        catch
        {
            e.State = CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    }

    public static PermissionKindKey Map(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Notifications => PermissionKindKey.Notifications,
        CoreWebView2PermissionKind.Camera => PermissionKindKey.Camera,
        CoreWebView2PermissionKind.Microphone => PermissionKindKey.Microphone,
        CoreWebView2PermissionKind.Geolocation => PermissionKindKey.Geolocation,
        CoreWebView2PermissionKind.ClipboardRead => PermissionKindKey.ClipboardRead,
        CoreWebView2PermissionKind.UnknownPermission => PermissionKindKey.Unknown,
        _ => PermissionKindKey.Other,
    };

    private async Task ConfigureAuthenticationAsync(CoreWebView2 core, WebView2Session session, BrowserStartRequest request, ProfileConfig config)
    {
        var ctx = request.Context;
        bool Current() => !session.IsClosing && request.IsCurrentGeneration(ctx);
        // The relay owns upstream proxy auth. Never use OS credentials or native dialogs.
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        await ClientHintsRequests.ForCore(core, Current,
            reason => StopAfterPrivacyFailureAsync(session, request, reason), stripClientHints: false).ConfigureAsync();
    }

    private async void HandleDownload(WebView2Session session, WebView2 view, BrowserStartRequest request, ProfileConfig config, CoreWebView2DownloadStartingEventArgs e)
    {
        var ctx = session.Context;
        bool Current() => !session.IsClosing && session.OwnsDownloadView(view) && request.IsCurrentGeneration(ctx);
        var deferral = e.GetDeferral();
        session.BeginDownload(view); // synchronously retain a tab closed while identity/save choice awaits
        try
        {
            // Keep the established native retry path. Safety prompts remain reachable
            // through explicit OpenDefaultDownloadDialog, including stalled finalization.
            e.Handled = true;
            if (!Current()) { e.Cancel = true; return; }
            session.DownloadStarted(view);
            var downloadId = await session.IdentifyDownloadAsync(view, e.DownloadOperation.Uri);
            if (!Current()) { e.Cancel = true; return; }
            if (session.FindResumingDownload(view, downloadId) is { } resuming)
            {
                e.ResultFilePath = resuming.FilePath;
                resuming.ReplaceOperation(e.DownloadOperation);
                return;
            }
            var suggested = DownloadPaths.SanitizeFileName(Path.GetFileName(e.ResultFilePath));
            var chosen = await session.ChooseDownloadPathAsync(view, downloadId, () => _host.ChooseDownloadPathAsync(ctx, suggested, config.DownloadDirectory));
            if (chosen is null || !Current())
            {
                e.Cancel = true;
                _host.ReportDownload(new DownloadInfo(ctx, downloadId ?? Guid.NewGuid(), suggested, DownloadPhase.Cancelled, "Сохранение отменено."));
                return;
            }
            e.ResultFilePath = chosen;
            if (session.FindResumingDownload(view, downloadId) is { } pendingTracker)
            {
                pendingTracker.ReplaceOperation(e.DownloadOperation);
                return;
            }
            var tracker = new BrowserDownloadTracker(e.DownloadOperation, _host, ctx, chosen, Current, downloadId,
                () => session.CompletedDownloadBytes(view, downloadId), session.OpenDownloadDetails);
            session.RegisterDownload(view, tracker);
            tracker.Start();
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            _host.ReportProblem(ctx, "Загрузка отменена из-за ошибки: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
            session.EndDownload(view);
        }
    }
}
