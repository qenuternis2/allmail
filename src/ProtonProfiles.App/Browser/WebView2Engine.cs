using System.Globalization;
using System.IO;
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

        if (!Enum.IsDefined(config.WebRtcPagePolicy) || !Enum.IsDefined(config.WebRtcNetworkPolicy))
            throw new BrowserStartException("Неизвестная политика WebRTC.", processMayExist: false);
        if (config.WebRtcNetworkPolicy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental)
        {
            if (!Capabilities.WebRtcNetworkRestrictionSupported)
                throw new BrowserStartException("Ограничение сети WebRTC недоступно в этой сборке.", processMayExist: false);
            if (HasBrowserArgumentOverrides())
                throw new BrowserStartException("Внешние настройки аргументов браузера мешают проверить сетевую политику WebRTC. Открытие заблокировано.", processMayExist: false);
        }

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
        // Assign the complete validated string once: an RTC flag must not replace the proxy flag.
        options.AdditionalBrowserArguments = BrowserArguments.Build(
            config.NetworkMode == NetworkMode.Proxy ? config.Proxy!.Endpoint! : null, config.WebRtcNetworkPolicy);

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: request.UserDataFolder, options: options);
        }
        catch (WebView2RuntimeNotFoundException e)
        {
            throw new BrowserStartException("Среда выполнения WebView2 не установлена.", processMayExist: false, inner: e);
        }
        catch (Exception e)
        {
            throw new BrowserStartException("Не удалось создать среду браузера: " + e.Message, processMayExist: false, inner: e);
        }

        // Subscribe before anything can shut the environment down (spec §4.4).
        var session = new WebView2Session(context, environment, _host) { Request = request, Config = config };

        // Step 3: the effective UDF and channel must match; policies or env vars can override supplied values (S23).
        if (!_paths.IsExpectedUserDataFolder(context.ProfileId, environment.UserDataFolder))
            throw new BrowserStartException("Фактическая папка данных браузера не совпадает с ожидаемой; открытие заблокировано.", processMayExist: false);
        if (!IsStableChannel(environment.BrowserVersionString))
            throw new BrowserStartException($"Неожиданный канал среды выполнения ({environment.BrowserVersionString}); открытие заблокировано.", processMayExist: false);

        cancellationToken.ThrowIfCancellationRequested(); // still no browser process

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

        if (cancellationToken.IsCancellationRequested) return session; // caller disposes it; never navigates

        // Step 5: handlers and settings before the first explicit navigation.
        var core = view.CoreWebView2;
        session.BrowserProcessId = (int)core.BrowserProcessId;
        session.RuntimeVersion = environment.BrowserVersionString;
        try
        {
            ConfigureProfile(core.Profile, config);
            await ConfigureWebViewAsync(session, core, config, request, controllerOptions);
        }
        catch (Exception e)
        {
            session.WebRtcGuardFailed = true;
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
        core.Navigate(_navigation.StartUri.AbsoluteUri);
        return session;
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
        o.ProfileName = BrowserProfileName;
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
    private async Task ConfigureWebViewAsync(WebView2Session session, CoreWebView2 core, ProfileConfig config, BrowserStartRequest request, CoreWebView2ControllerOptions controllerOptions, bool childWindow = false)
    {
        var ctx = request.Context;
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
        // Default mode never sets the property, so the current Runtime's native UA is used (spec §5, A07).
        if (config.UserAgentMode == UserAgentMode.Custom) s.UserAgent = config.CustomUserAgent;

        core.NavigationStarting += (_, e) =>
        {
            if (session.IsClosing || !request.IsCurrentGeneration(ctx)) { e.Cancel = true; return; }
            var decision = _navigation.EvaluateTopLevel(e.Uri);
            if (decision == TopLevelDecision.Allow) return;
            e.Cancel = true;
            // Never auto-launch; offer an explicit action and never for service URLs (tokens).
            if (decision == TopLevelDecision.BlockOfferExternal && e.IsUserInitiated && !_navigation.IsServiceUrl(e.Uri))
                _host.OfferExternalLink(ctx, e.Uri);
        };

        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            var failed = false;
            try
            {
                e.Handled = true;
                if (session.IsClosing || !request.IsCurrentGeneration(ctx)) return;
                var target = e.Uri;
                var allowed = _navigation.EvaluateTopLevel(target) == TopLevelDecision.Allow
                              || (target.StartsWith("blob:", StringComparison.Ordinal) && _navigation.EvaluateTopLevel(target[5..]) == TopLevelDecision.Allow);
                if (!allowed)
                {
                    if (NavigationPolicy.IsExternalLaunchable(target) && !_navigation.IsServiceUrl(target)) _host.OfferExternalLink(ctx, target);
                    return; // cancelled: Handled without NewWindow
                }
                // Same environment and profile, unnavigated child (S18).
                var child = await session.CreateChildWindowAsync(controllerOptions);
                if (child is null || session.IsClosing || !request.IsCurrentGeneration(ctx)) return;
                ConfigureProfile(child.CoreWebView2.Profile, config);
                await ConfigureWebViewAsync(session, child.CoreWebView2, config, request, controllerOptions, childWindow: true);
                child.ZoomFactor = config.ZoomFactor;
                await AttachNetworkLogAsync(child.CoreWebView2, session, "дочернее окно");
                if (session.IsClosing || !request.IsCurrentGeneration(ctx)) return;
                e.NewWindow = child.CoreWebView2;
            }
            catch (Exception ex)
            {
                _host.ReportProblem(ctx, "Не удалось открыть дочернее окно: " + ex.Message);
                failed = true;
            }
            finally
            {
                deferral.Complete();
            }
            if (failed) await FailWebRtcProfileAsync(session, request);
        };

        core.PermissionRequested += (_, e) => HandlePermission(ctx, request, config, e);
        core.FrameCreated += (_, f) => f.Frame.PermissionRequested += (_, e) => HandlePermission(ctx, request, config, e);

        core.BasicAuthenticationRequested += (_, e) => HandleBasicAuth(session, ctx, request, config, e);

        core.DownloadStarting += (_, e) => HandleDownload(ctx, request, config, e);

        core.ProcessFailed += (_, e) =>
        {
            if (!request.IsCurrentGeneration(ctx)) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return; // BrowserProcessExited drives lifecycle
            _host.ReportProblem(ctx, $"Сбой процесса браузера: {e.ProcessFailedKind}.");
        };
        await InstallPageGuardAsync(core, session, config, request, preserveUnnavigated: childWindow);
    }

    /// <summary>Used by every profile controller, including the isolated bundled diagnostic page.</summary>
    private async Task InstallPageGuardAsync(CoreWebView2 core, WebView2Session session, ProfileConfig config, BrowserStartRequest request, bool preserveUnnavigated = false)
    {
        if (config.WebRtcPagePolicy != WebRtcPagePolicy.Block) return;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcPageGuard.Script);
        // NewWindow requires an unnavigated controller. For children, await registration before assigning it;
        // its first document is observed below. Never bootstrap-navigate a pending NewWindow controller.
        if (!preserveUnnavigated)
        {
            // Registration applies only to future documents. Bootstrap an owned blank document, with no target scripts,
            // and verify actual injection before any target navigation (also for child/persisted-profile controllers).
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
            if (session.IsClosing || !request.IsCurrentGeneration(request.Context))
                throw new InvalidOperationException("Профиль закрывается.");
            if (await core.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript) != "true")
                throw new InvalidOperationException("Блокировка WebRTC в начальном документе не подтверждена.");
        }
        session.WebRtcGuardRegistrations++;

        // These late observations detect failures; they cannot retroactively prevent earlier traffic.
        core.DOMContentLoaded += async (_, _) => await ObserveGuardAsync(() => core.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript), session, request);
        core.FrameCreated += (_, e) =>
        {
            var frame = e.Frame;
            frame.DOMContentLoaded += async (_, _) =>
            {
                if (frame.IsDestroyed() != 0) return;
                await ObserveGuardAsync(() => frame.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript), session, request, () => frame.IsDestroyed() != 0);
            };
        };
    }

    private async Task ObserveGuardAsync(Func<Task<string>> observe, WebView2Session session, BrowserStartRequest request, Func<bool>? destroyed = null)
    {
        if (session.IsClosing || !request.IsCurrentGeneration(request.Context)) return;
        try
        {
            if (await observe() == "true") return;
        }
        catch (Exception)
        {
            if (session.IsClosing || destroyed?.Invoke() == true || !request.IsCurrentGeneration(request.Context)) return;
        }
        await FailWebRtcProfileAsync(session, request);
    }

    private async Task FailWebRtcProfileAsync(WebView2Session session, BrowserStartRequest request)
    {
        if (session.IsClosing) return;
        session.WebRtcGuardFailed = true;
        // Stop new contexts immediately, then let the lifecycle gate await this generation's process exit.
        await session.CloseAsync();
        await _host.StopProfileAsync(request.Context, "Проверка защиты WebRTC не пройдена. Открытие заблокировано; проверьте совместимость среды выполнения.");
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

    public const string ProbeHost = "probe.protonprofiles.invalid";
    public static string ProbeUri => $"https://{ProbeHost}/fingerprint.html";

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
        var folder = Path.Combine(_paths.Root, "Probe");
        try
        {
            Directory.CreateDirectory(folder);
            using (var resource = typeof(WebView2Engine).Assembly.GetManifestResourceStream("ProtonProfiles.App.Diagnostics.fingerprint.html")
                                  ?? throw new InvalidOperationException("Страница проверки не найдена в сборке."))
            using (var file = new FileStream(Path.Combine(folder, "fingerprint.html"), FileMode.Create, FileAccess.Write, FileShare.Read))
                await resource.CopyToAsync(file);

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
        // The UA is a per-view setting: mirror the profile so the report matches the mail view.
        if (config.UserAgentMode == UserAgentMode.Custom) s.UserAgent = config.CustomUserAgent;
        core.SetVirtualHostNameToFolderMapping(ProbeHost, folder, CoreWebView2HostResourceAccessKind.Deny);

        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank") return; // owned guard bootstrap only; no report bridge accepts this origin
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !string.Equals(u.Host, ProbeHost, StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) =>
        {
            e.SavesInProfile = false;
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.BasicAuthenticationRequested += (_, e) => HandleBasicAuth(session, ctx, request, config, e);
        core.WebMessageReceived += (_, e) =>
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var u) || !string.Equals(u.Host, ProbeHost, StringComparison.OrdinalIgnoreCase)) return;
            try { onReport(e.TryGetWebMessageAsString()); } catch (ArgumentException) { }
        };
        await AttachNetworkLogAsync(core, session, "проверка отпечатка");
        try { await InstallPageGuardAsync(core, session, config, request); }
        catch (Exception)
        {
            await FailWebRtcProfileAsync(session, request);
            return "Не удалось применить блокировку WebRTC к странице проверки.";
        }
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
            var allowed = await _permissions.ResolveAsync(ctx, e.Uri, Map(e.PermissionKind), (origin, kind) => _host.AskPermissionAsync(ctx, origin, kind), config.WebRtcPagePolicy);
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

    private void HandleBasicAuth(WebView2Session session, GenerationContext ctx, BrowserStartRequest request, ProfileConfig config, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        // The event also covers website 401s; proxy credentials go only to this generation's exact proxy endpoint (spec §6.2).
        var decision = ProxyChallengeMatcher.Evaluate(config.Proxy?.Endpoint, e.Uri, isProxyChallenge: config.NetworkMode == NetworkMode.Proxy);
        if (decision != ProxyChallengeMatcher.Decision.ReleaseProxyCredentials)
        {
            // Not our proxy: never supply proxy credentials. Cancel so no default credential prompt leaks anything.
            e.Cancel = true;
            return;
        }
        if (!request.IsCurrentGeneration(ctx) || config.Proxy?.AuthMode != ProxyAuthMode.Basic || config.Proxy.CredentialRef is null)
        {
            e.Cancel = true;
            return;
        }
        if (!session.ProxyAuthBudget.TryConsume())
        {
            e.Cancel = true;
            _host.ReportProblem(ctx, "Прокси отклонил учётные данные. Проверьте логин и пароль в настройках профиля.");
            return;
        }
        var credential = _credentials.Read(config.Proxy.CredentialRef);
        if (credential is null)
        {
            e.Cancel = true;
            _host.ReportProblem(ctx, "Учётные данные прокси не найдены.");
            return;
        }
        e.Response.UserName = credential.UserName;
        e.Response.Password = credential.Password;
    }

    private async void HandleDownload(GenerationContext ctx, BrowserStartRequest request, ProfileConfig config, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true; // our UI replaces the default download flyout
            if (!request.IsCurrentGeneration(ctx)) { e.Cancel = true; return; }
            var suggested = DownloadPaths.SanitizeFileName(Path.GetFileName(e.ResultFilePath));
            var chosen = await _host.ChooseDownloadPathAsync(ctx, suggested, config.DownloadDirectory);
            if (chosen is null || !request.IsCurrentGeneration(ctx))
            {
                e.Cancel = true;
                _host.ReportDownload(new DownloadInfo(ctx, suggested, DownloadPhase.Cancelled, "Отменено пользователем"));
                return;
            }
            e.ResultFilePath = chosen;
            var op = e.DownloadOperation;
            var name = Path.GetFileName(chosen);
            _host.ReportDownload(new DownloadInfo(ctx, name, DownloadPhase.InProgress, null));
            op.StateChanged += (_, _) =>
            {
                var phase = op.State switch
                {
                    CoreWebView2DownloadState.Completed => DownloadPhase.Completed,
                    CoreWebView2DownloadState.Interrupted => op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? DownloadPhase.Cancelled : DownloadPhase.Interrupted,
                    _ => DownloadPhase.InProgress,
                };
                _host.ReportDownload(new DownloadInfo(ctx, name, phase, phase == DownloadPhase.Interrupted ? op.InterruptReason.ToString() : null));
            };
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            _host.ReportProblem(ctx, "Загрузка отменена из-за ошибки: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
