# ADR-001. Браузерный движок и сетевой API

Статус: **принято для Core и Experimental Proxy; решение для Production Candidate с прокси — открыто**
Дата: 3 октября 2026 · Основание: спецификация v1.1, §2, §6, §12

## Контекст

Спецификация требует WebView2 с отдельными `CoreWebView2Environment` и `UserDataFolder` на каждый профиль и отдельный сетевой маршрут (System или Proxy) на профиль. Для прокси нужен документированный API нужного уровня; флаг `--proxy-server` через `AdditionalBrowserArguments` Microsoft не рекомендует для продакшена [S6].

## Что проверено

Проверка выполнялась по пакету `Microsoft.Web.WebView2` **1.0.4258.31** (последняя стабильная версия на nuget.org на 3 октября 2026) — по XML-документации и метаданным сборки `Microsoft.Web.WebView2.Core`:

| Требование | Найденный API | Вывод |
| --- | --- | --- |
| Изоляция хранилища на профиль | `CoreWebView2Environment.CreateAsync(null, udf, options)` | Есть, используется |
| Эксклюзивный доступ к UDF | `CoreWebView2EnvironmentOptions.ExclusiveUserDataFolderAccess` | Есть, `true` |
| Язык браузера | `CoreWebView2EnvironmentOptions.Language` | Есть |
| Локаль JS | `CoreWebView2ControllerOptions.ScriptLocale` | Есть |
| Освобождение UDF | `CoreWebView2Environment.BrowserProcessExited` | Есть |
| Аутентификация прокси | `CoreWebView2.BasicAuthenticationRequested` (`Uri` указывает на прокси для proxy-вызовов [S14]) | Есть, но без явного признака «это 407» |
| **Прокси на окружение/профиль** | Свойств вида `Proxy*` в `CoreWebView2EnvironmentOptions`, `CoreWebView2ControllerOptions`, `CoreWebView2Profile` **не найдено** | **Документированного API нет** |

Поиск: все члены в `Microsoft.Web.WebView2.Core.xml`, в описании которых встречается слово «proxy». Найдено 10: `BasicAuthenticationRequested` и его `Uri`, `CoreWebView2ClientCertificateRequestedEventArgs.IsProxy`, `CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired`, а также упоминания в `AddHostObjectToScript`, `NavigationCompleted.IsSuccess`, `NewWindowRequested.*` и `ServiceWorkerRegistered`, где «proxy» означает объект-посредник, а не сетевой прокси. Ни одно из них не задаёт маршрут.

## Решение

1. **Core build** — WebView2, режим сети только System. Профиль в режиме Proxy остаётся заблокированным со статусом «Прокси не поддерживается в этой сборке» и **никогда не открывается как System** (`NetworkReadinessEvaluator`, тест `Proxy_profile_is_blocked_in_core_build_and_never_opens_as_system`).
2. **Experimental proxy build** — `dotnet build -p:ExperimentalProxy=true`. Флаг `--proxy-server=http://host:port` собирается только из разобранных полей (`ProxyArguments`), пользовательский ввод не может добавить другие флаги. В интерфейсе постоянный жёлтый баннер «Экспериментальная сборка», имя исполняемого файла `ProtonProfiles.ExperimentalProxy.exe`. Поддерживается только HTTP-прокси с CONNECT и аутентификацией None/Basic.
3. **Production Candidate с прокси** — не заявляется. Варианты, которые нужно оценить отдельным решением после прогонов A09–A13, A24, A31 на Windows:
   - дождаться документированного per-environment proxy API в WebView2 и перейти на него (замена ограничена `WebView2Engine` + `BrowserCapabilities.ProxySupport = DocumentedApi`);
   - перейти на Electron: постоянная `session` на профиль и `session.setProxy()` + `closeAllConnections()` [S13]. Объём миграции: заменить `ProtonProfiles.App` (WPF/WebView2) на Electron-оболочку; ядро логики (валидация, импорт/экспорт, напоминания, политика разрешений, жизненный цикл) переносится как спецификация поведения или через отдельный процесс .NET. Папки WebView2 не переносятся — только метаданные, вход в Proton заново. Матрицу A01–A32 нужно повторить;
   - сетевую изоляцию на уровне ОС/ВМ (§6.4, §12), если требуется запрет любых соединений вне маршрута.

## Последствия

- Граница замены — интерфейс `IBrowserEngine`/`IBrowserSession` и запись `BrowserCapabilities` в `ProtonProfiles.Core`. Универсального многодвижкового фреймворка нет.
- Разные UA, языки или IP не делают браузерные отпечатки независимыми и не являются критерием приёмки.
- Флаг может измениться или исчезнуть с обновлением Evergreen Runtime; после каждого обновления Runtime сетевые проверки нужно повторять.
