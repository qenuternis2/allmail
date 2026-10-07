# Манифест возможностей

Исторический манифест на 3 октября 2026, до первых Windows-прогонов. Пустой столбец
«Проверено» относится к состоянию на эту дату. Текущие автоматические проверки и
их границы описаны в [README нативного стенда](../tests/ProtonProfiles.WebView2.Smoke/README.md)
и протоколах Windows CI; эта таблица не заменяет ручную приёмку.

Закреплённые версии: .NET SDK 10.0.1xx (`global.json`), `Microsoft.Web.WebView2` 1.0.4258.31, `Microsoft.Data.Sqlite` 10.0.12.

| API | Где используется | Статус | Проверено (SDK / Runtime) |
| --- | --- | --- | --- |
| `CoreWebView2Environment.CreateAsync(browserFolder, udf, options)` | `WebView2Engine.StartAsync` | Реализовано | — |
| `CoreWebView2EnvironmentOptions.Language` | язык браузера | Реализовано | — |
| `CoreWebView2EnvironmentOptions.ExclusiveUserDataFolderAccess = true` | §4.2 | Реализовано | — |
| `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount = false` | §4.2 | Реализовано | — |
| `CoreWebView2EnvironmentOptions.AreBrowserExtensionsEnabled = false` | §4.2 | Реализовано | — |
| `CoreWebView2EnvironmentOptions.EnableTrackingPrevention = true` | §5 | Реализовано | — |
| `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` (`--proxy-server`) | только Experimental | Экспериментально | — |
| `CoreWebView2Environment.UserDataFolder`, `BrowserVersionString` | проверка фактического UDF и канала | Реализовано | — |
| `CoreWebView2Environment.BrowserProcessExited` | ожидание освобождения UDF | Реализовано | — |
| `CoreWebView2Environment.GetAvailableBrowserVersionString()` | обнаружение Runtime | Реализовано | — |
| `CoreWebView2ControllerOptions.ProfileName`, `IsInPrivateModeEnabled = false` | постоянный профиль | Реализовано | — |
| `CoreWebView2ControllerOptions.ScriptLocale` | локаль JS | Реализовано | — |
| `WebView2.EnsureCoreWebView2Async(environment, controllerOptions)` (WPF) | явная инициализация | Реализовано | — |
| `CoreWebView2Settings.UserAgent` | Custom UA | Реализовано | — |
| `CoreWebView2Settings.AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false` | §7 | Реализовано | — |
| `CoreWebView2Profile.PreferredColorScheme` | тема (Системная → Auto) | Реализовано | — |
| `CoreWebView2Profile.PreferredTrackingPreventionLevel` | Balanced/Strict | Реализовано | — |
| `CoreWebView2Profile.IsPasswordAutosaveEnabled = false`, `IsGeneralAutofillEnabled = false` | §7 | Реализовано | — |
| `CoreWebView2.NewWindowRequested` + deferral + `NewWindow` | дочерние окна | Реализовано | — |
| `CoreWebView2.PermissionRequested`, `CoreWebView2Frame.PermissionRequested`, `SavesInProfile = false` | разрешения | Реализовано | — |
| `CoreWebView2.BasicAuthenticationRequested` | аутентификация прокси | Экспериментально | — |
| `CoreWebView2.DownloadStarting`, `DownloadOperation.StateChanged` | вложения | Реализовано | — |
| `CoreWebView2.NavigationStarting` | политика навигации | Реализовано | — |
| `CoreWebView2.ProcessFailed` | диагностика | Реализовано | — |
| Windows Credential Manager (`CredWriteW`/`CredReadW`/`CredDeleteW`/`CredEnumerateW`) | секреты прокси | Реализовано | — |
