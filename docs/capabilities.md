# Манифест возможностей — SecureBrowser 0.1.51

Поддерживаемая проверяемая базовая версия: WebView2 SDK **1.0.4258.31**,
Evergreen Runtime **154+** (фактическая полная версия записывается в Windows CI).
Это минимальная поддерживаемая проектом конфигурация для перечисленных API;
она не означает, что каждый API впервые появился в этой версии. Более старые
Runtime не сертифицированы: ошибки API/защиты блокируют профиль, без тихого fallback.
SDK сборки закреплён; новейшая версия проверяемого Runtime может меняться.
Полный Windows-прогон 0.1.50 подтверждён: SDK 1.0.4258.31 / Runtime 154.0.4258.62;
ссылки и границы сценариев приведены в матрице приёмки.
[Матрица и ограничения](acceptance-report-0.1.51.md).

Закреплённые версии: .NET SDK 10.0.100 (`global.json`, rollForward latestFeature), `Microsoft.Web.WebView2` 1.0.4258.31, `Microsoft.Data.Sqlite` 10.0.12.

| API | Где используется | Статус | Проверено (SDK / Runtime) |
| --- | --- | --- | --- |
| `CoreWebView2Environment.CreateAsync(browserFolder, udf, options)` | `WebView2Engine.StartAsync` | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.Language` | язык браузера | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.ExclusiveUserDataFolderAccess = true` | §4.2 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount = false` | §4.2 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.AreBrowserExtensionsEnabled = false` | §4.2 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.EnableTrackingPrevention = true` | §5 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` (`--proxy-server`) | только Experimental | Экспериментально | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Environment.UserDataFolder`, `BrowserVersionString` | проверка фактического UDF и канала | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Environment.BrowserProcessExited` | ожидание освобождения UDF | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Environment.GetAvailableBrowserVersionString()` | обнаружение Runtime | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2ControllerOptions.IsInPrivateModeEnabled = false` (ProfileName оставлен нативным Default) | постоянный профиль | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2ControllerOptions.ScriptLocale` | локаль JS | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `WebView2.EnsureCoreWebView2Async(environment, controllerOptions)` (WPF) | явная инициализация | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Settings.UserAgent` | Custom UA | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Settings.AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false` | §7 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Profile.PreferredColorScheme` | тема (Системная → Auto) | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Profile.PreferredTrackingPreventionLevel` | Balanced/Strict | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2Profile.IsPasswordAutosaveEnabled = false`, `IsGeneralAutofillEnabled = false` | §7 | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.NewWindowRequested` + deferral + `NewWindow` | дочерние окна | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.PermissionRequested`, `CoreWebView2Frame.PermissionRequested`, `SavesInProfile = false` | разрешения | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.BasicAuthenticationRequested` | аутентификация прокси | Экспериментально | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.DownloadStarting`, `DownloadOperation.StateChanged` | вложения | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.NavigationStarting` | политика навигации | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| `CoreWebView2.ProcessFailed` | диагностика | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |
| Windows Credential Manager (`CredWriteW`/`CredReadW`/`CredDeleteW`/`CredEnumerateW`) | секреты прокси | Реализовано | 1.0.4258.31 / Runtime 154+, Windows CI |

## Дополнительные документированные SDK API

Для каждой строки поддерживаемый минимум — SDK 1.0.4258.31 / Runtime 154+;
минимумы для старых Runtime не устанавливались экспериментально.

| API / члены | Использование и проверка |
| --- | --- |
| `CreateCoreWebView2ControllerOptions`, `CoreWebView2ControllerOptions.ScriptLocale/IsInPrivateModeEnabled`, `CoreWebView2Profile.ProfileName/ProfilePath` | production startup/tab fixtures; постоянный Runtime Default внутри отдельного UDF. ProfileName намеренно не назначается: named BrowserContext мешал native permission overrides |
| `CoreWebView2Settings.AreDevToolsEnabled/AreDefaultContextMenusEnabled/AreBrowserAcceleratorKeysEnabled/IsStatusBarEnabled/IsZoomControlEnabled` | настройки каждого контроллера; UI/browser smoke |
| `CoreWebView2Profile.GetNonDefaultPermissionSettingsAsync/SetPermissionStateAsync`, `CoreWebView2PermissionSetting.PermissionKind/PermissionState/PermissionOrigin` | удаление конфликтующих hardware grants; `PermissionRequestsSmoke`, bootstrap guards |
| `CoreWebView2PermissionRequestedEventArgs.Uri/PermissionKind/State/SavesInProfile/GetDeferral/Handled`, `CoreWebView2Frame.Destroyed/FrameCreated/PermissionRequested`, `CoreWebView2Deferral.Complete` | одна application policy, frame/top-level handlers, async deferrals |
| `CoreWebView2.GetDevToolsProtocolEventReceiver`, `CallDevToolsProtocolMethodAsync`, `CallDevToolsProtocolMethodForSessionAsync` | CDP commands/events ниже; версия/targets требуют readback, не гарантируется универсальное покрытие |
| `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync/RemoveScriptToExecuteOnDocumentCreated`, `ExecuteScriptAsync` | WebRTC/privacy guards и probe; новые документы/frames; dedicated workers через CDP |
| `CoreWebView2.Navigate/NavigateToString/Stop/Reload/GoBack/GoForward`, `Source/CanGoBack/CanGoForward`, `NavigationStarting/NavigationCompleted/SourceChanged/HistoryChanged/DocumentTitleChanged/WindowCloseRequested` | production browser tabs, reload/history/close; исходная navigation до readback не разрешена |
| `CoreWebView2NavigationCompletedEventArgs.NavigationId/IsSuccess/WebErrorStatus/HttpStatusCode` | proxy startup connectivity; no cache/SW result; 401/403/429 ответа не блокируют пользовательскую проверку |
| `CoreWebView2DownloadStartingEventArgs.DownloadOperation/ResultFilePath/Cancel/Handled/GetDeferral` | production downloads, отмена save dialog, blob/HTTP |
| `CoreWebView2DownloadOperation.BytesReceived/TotalBytesToReceive/State/InterruptReason/CanResume/BytesReceivedChanged/StateChanged/Pause/Resume/Cancel` | status/progress, pause/resume, closed-origin controller ownership; exact payload fixtures |
| `CoreWebView2.BrowserProcessId`, `CoreWebView2BrowserProcessExitedEventArgs.BrowserProcessExitKind` | retained exit signal, exact owned fixture PID crash, 20 циклов; чужие процессы не убиваются |
| `WebView2.CoreWebView2/CoreWebView2InitializationCompleted/ZoomFactor/Dispose` | все контроллеры явные, WPF STA; actual disposal/restart smoke |

## CDP команды и события — экспериментальная зависимость

Используемые команды: `Runtime.evaluate`, `Runtime.runIfWaitingForDebugger`,
`Target.setAutoAttach`, `Target.getTargets`, `Target.closeTarget`,
`Emulation.setUserAgentOverride`, `Emulation.setTimezoneOverride`,
`Emulation.setHardwareConcurrencyOverride`, `Emulation.setEmulatedMedia`,
`Emulation.setEmulatedOSTextScale`, `Emulation.setDevicePostureOverride`,
`Page.setFontFamilies`, `Page.setFontSizes`, `Page.enable`,
`Browser.setPermission`, `Fetch.enable`, `Fetch.continueRequest`,
`Fetch.failRequest`, `Network.enable`, `Network.setBypassServiceWorker`,
`Network.setCacheDisabled`.
События: `Target.attachedToTarget/detachedFromTarget`, `Fetch.requestPaused`,
`Page.downloadWillBegin`, `Network.requestWillBeSent/responseReceived/loadingFailed/loadingFinished`.
Команды в связанных targets также вызываются через session API. Их успешный
вызов проверяется до первого скрипта; native smoke проверяет observations
в main/dedicated worker/frames. SharedWorker при разрешённом исключении и
часть persisted Service Worker coverage остаются непроверенными.

`AdditionalBrowserArguments` использует runtime-dependent Chromium/Blink flags
для proxy/WebRTC/graphics/privacy. Закреплённые имена и их сборка —
`Core/Network/ProxyArguments.cs` (также `BrowserArguments`) и privacy
modules. SDK поддерживает передачу строки, но это не поддерживаемый production
контракт конкретных Chromium-флагов. Полное сетевое и fingerprint покрытие не
следует из компиляции либо readback одного API.

## Windows/упаковка

Win32 Credential Manager проверяется в Windows; `Get-CimInstance Win32_Process`
используется только для безопасного отказа uninstall cleanup при оставшемся
Runtime. FileShare leases позволяют нескольким app instances читать Root и
исключают одновременное удаление данных. Inno Setup 7.1.0 закреплён SHA-256;
installer smoke проверяет install/update/keep/remove. Optional Authenticode
подпись не выполняется без настоящего сертификата.


Язык браузера задаётся документированным `EnvironmentOptions.Language`, а
`ScriptLocale` — отдельно для Intl. В Runtime 154.0.4258.62 запрос `de-DE`
наблюдается как `navigator.language = "de"` и `Intl locale = "de-DE"`: Runtime
может выбрать нативный язык интерфейса без регионального суффикса. Приложение
не заменяет этот getter JavaScript-обёрткой. Полный сетевой Accept-Language
для restore-defaults A/B пока не проверен; точное совпадение регионального
суффикса во всех этих API не заявляется.
