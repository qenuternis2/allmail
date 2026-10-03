# Отчёт по критериям приёмки A01–A32

Сборка: 0.1.0 (этап 1–2, прототип) · Дата: 3 октября 2026

## Окружение проверки

| Параметр | Значение |
| --- | --- |
| Где выполнялось | Linux-контейнер (Ubuntu 24.04 x64). **Windows и WebView2 Runtime недоступны** |
| .NET SDK | 10.0.112 (Ubuntu-пакет `dotnet-sdk-10.0`) |
| Microsoft.Web.WebView2 (SDK) | 1.0.4258.31 |
| Microsoft.Data.Sqlite | 10.0.12 |
| WebView2 Runtime | не проверялся (нет Windows) |
| Windows build | не проверялся |
| Прокси, стенд | HTTPS-стенд `tests/fixture` проверен только в headless Chromium 141 как самопроверка страницы |
| Способ наблюдения сети | не применялся (нет Windows) |

Что сделано в этом окружении: `ProtonProfiles.App` компилируется для `net10.0-windows` (обе сборки: основная и `-p:ExperimentalProxy=true`) через `EnableWindowsTargeting`; 161 модульный тест ядра проходит (`dotnet test`). **Компиляция на Linux не подтверждает поведение WebView2.** Все пункты, зависящие от реального браузера, отмечены как невыполненные подпроверки, поэтому родительский критерий не может быть Pass.

Статусы: Pass, Fail, Blocked, NotApplicable. Для подпроверок: «логика ✔» — проверено модульным тестом ядра; «Windows ✖» — не выполнено, требует Windows + WebView2 Runtime.

## Сводка

| Статус | Количество |
| --- | --- |
| Pass | 0 |
| Fail | 0 |
| Blocked | 25 |
| NotApplicable (Core build) | 7 (A09–A13, A24, A31; для полной цели с прокси — Blocked) |

## Критерии

| ID | Статус | Подпроверки и доказательства | Причина |
| --- | --- | --- | --- |
| A01 | Blocked | логика ✔: UDF выводится только из UUID, у профилей разные UDF (`LifecycleTests.Two_profiles_get_distinct_udfs_and_generations`, `StorageTests.Paths_derive_only_from_uuid`). Стенд ✔ в Chromium: cookie/LocalStorage/IndexedDB/CacheStorage A и B не смешиваются и переживают перезапуск. Windows ✖: то же в WebView2 | Нет Windows |
| A02 | Blocked | Стенд содержит SW и BroadcastChannel; в Chromium с недоверенным сертификатом SW не регистрируется (ожидаемо, нужен доверенный тестовый сертификат). Windows ✖ | Нет Windows |
| A03 | Blocked | логика ✔: межпроцессная блокировка, отказ второму владельцу, освобождение после «краха» процесса (`StorageTests.Lock_held_by_another_process_blocks_until_it_dies`, `Lock_rejects_second_holder_and_releases`). Windows ✖: два экземпляра приложения | Нет Windows |
| A04 | Blocked | логика ✔: сброс/удаление A не трогают данные, разрешения и состояние B (`LifecycleTests.Reset_removes_only_udf_and_permissions_of_that_profile`, `Delete_removes_metadata_secrets_and_udf_but_not_external_downloads`). Windows ✖: выход на сайте и вход B после этого | Нет Windows |
| A05 | Blocked | Реализовано: `NewWindowRequested` с deferral, дочерний WebView на том же `environment` и тех же controller options (`WebView2Session.CreateChildWindowAsync`). Windows ✖ | Нет Windows |
| A06 | Blocked | Реализовано: UA задаётся только в режиме Custom на каждом WebView профиля, включая дочерние. Стенд выводит HTTP UA, `navigator.userAgent`, Client Hints. Windows ✖: документ, iframe, worker, ограничения Client Hints | Нет Windows |
| A07 | Blocked | логика ✔: смена UA требует перезапуска окружения, новая генерация стартует без override (`SettingsRevisionTests.Successful_restart_commits_pending_revision`). Windows ✖: нативный UA после возврата в Default | Нет Windows |
| A08 | Blocked | логика ✔: язык/локаль — перезапуск, тема/масштаб — на лету (`ValidationTests.Restart_required_only_for_engine_level_settings`). Windows ✖ | Нет Windows |
| A09 | NotApplicable (Core) / Blocked (прокси) | Core: прокси-профиль заблокирован (`LifecycleTests.Proxy_profile_is_blocked_in_core_build_and_never_opens_as_system`). Experimental: флаг собирается только из полей (`ProxyTests.Flag_is_built_from_parsed_fields_only`). Windows ✖: наблюдение маршрутов | Нет Windows; документированного API нет (ADR-001) |
| A10 | NotApplicable (Core) / Blocked (прокси) | Windows ✖ | То же |
| A11 | NotApplicable (Core) / Blocked (прокси) | логика ✔: смена сети = перезапуск окружения с ожиданием выхода. Windows ✖: отсутствие повторного использования соединений | То же |
| A12 | NotApplicable (Core) / Blocked (прокси) | логика ✔: учётные данные отдаются только точному endpoint прокси, не сайтам с 401, повтор ограничен (`ProxyTests.Credentials_are_released_only_to_the_exact_proxy`, `Auth_retry_is_bounded`). Windows ✖: реальные 407/401 | То же |
| A13 | NotApplicable (Core) / Blocked (прокси) | Windows ✖ | То же |
| A14 | Blocked | логика ✔: решения по разрешениям раздельны по профилям, только «Всегда» сохраняется (`PermissionTests.Decisions_are_per_profile_and_only_always_is_stored`); вложения не запускаются (кода запуска нет). Windows ✖ | Нет Windows |
| A15 | Blocked | логика ✔: неожиданный выход процесса закрывает только A, папка не очищается, B открыт (`LifecycleTests.Unexpected_crash_closes_profile_without_touching_data_or_others`). Windows ✖: реальный крах процесса | Нет Windows |
| A16 | Blocked | логика ✔: экспорт без UUID, путей, ссылок на секреты, адресов прокси по умолчанию; диагностика без идентификаторов и секретов; импорт с новыми UUID (`InterchangeTests.Export_omits_secrets_ids_paths_and_matches_schema`, `Import_assigns_fresh_ids_and_no_trust_or_session_state`, `DiagnosticsTests.*`). Windows ✖: экспорт из приложения | Нет Windows |
| A17 | Blocked | логика ✔: календарные месяцы с обрезкой конца месяца, базовый часовой пояс, независимое откладывание, отмена ошибочной отметки (`ReminderTests.*`). Windows ✖: проверка интерфейса | Нет Windows |
| A18 | Blocked | логика ✔: 100 синтетических записей, поиск p95 < 200 мс на ядре (`RepositoryTests.Hundred_synthetic_profiles_search_within_budget`, фактически ~65 мс на весь тест); лимит 3 окружений (`LifecycleTests.Fourth_profile_requires_explicit_choice_including_starting`). Windows ✖: процессы браузера, отклик интерфейса | Нет Windows |
| A19 | Blocked | Реализовано: проверка Runtime при запуске, ссылка на Evergreen-установщик, данные не трогаются. Windows ✖ | Нет Windows |
| A20 | Blocked | — | not performed: account access unavailable |
| A21 | Blocked | логика ✔: склейка повторных открытий, отменённая инициализация утилизируется, старые генерации отвергаются, не больше одной живой генерации (`LifecycleTests.Duplicate_opens_coalesce_into_one_generation`, `Cancelled_initialization_is_disposed_not_resurrected`, `Stale_generation_is_rejected_after_restart`, `Rapid_operations_never_leave_two_live_generations`). Windows ✖ | Нет Windows |
| A22 | Blocked | Реализовано: нет `Source` в XAML, явный `EnsureCoreWebView2Async(environment, controllerOptions)`, deferral завершаются в `finally`. Windows ✖ | Нет Windows |
| A23 | Blocked | логика ✔: таймаут ожидания выхода → RecoveryRequired, блокировка и UDF сохраняются, повтор ждёт тот же сигнал (`LifecycleTests.Exit_timeout_enters_recovery_keeps_lock_and_udf`). Процессы `msedgewebview2.exe` не завершаются (такого кода нет). Windows ✖ | Нет Windows |
| A24 | NotApplicable (Core) / Blocked (прокси) | логика ✔: pending/applied ревизии, явный откат (`SettingsRevisionTests.*`); новый секрет получает новую ссылку. Windows ✖ | Нет Windows; нет API |
| A25 | Blocked | логика ✔: ссылки внутри дерева удаляются без перехода, каталог-ссылка отвергается, выход за пределы отвергается, внешняя папка загрузок сохраняется, прерванное удаление возобновляется (`StorageTests.*`, `LifecycleTests.Interrupted_delete_resumes_at_startup_without_opening`). Не проверено: заблокированный файл (на Linux от root блокировку удаления не воспроизвести), junction NTFS. Windows ✖ | Нет Windows |
| A26 | Blocked | логика ✔: битый/большой JSON, неизвестные поля и версии, дубли ключей, неверные значения, >1000 записей, нет частичного импорта, Unconfigured и прокси без адреса остаются заблокированными; схема и валидатор согласованы (`InterchangeTests.*`, 30+ случаев). Windows ✖: диалог предпросмотра | Нет Windows |
| A27 | Blocked | логика ✔: одна политика, дедупликация frame/top-level, сброс отзывает решения (`PermissionTests.*`); `SavesInProfile = false` в коде. Windows ✖ | Нет Windows |
| A28 | Blocked | логика ✔: разрешение локали скриптов, Auto/Light/Dark ↔ API (`ValidationTests.Script_locale_resolution`, `WebView2Engine.ToApi`). Стенд выводит Accept-Language, navigator, Intl, timeZone. Windows ✖ | Нет Windows |
| A29 | Blocked | логика ✔: внешний запуск только http/https по действию пользователя, имена вложений очищаются (`NavigationTests.*`, `DownloadTests.*`). Windows ✖: blob-вложения, отмена | Нет Windows |
| A30 | Blocked | логика ✔: без молчаливого вытеснения, закреплённые и запускающиеся не предлагаются, лимит учитывает Starting. Windows ✖: диалог с несохранённым вводом и загрузкой | Нет Windows |
| A31 | NotApplicable (Core) / Blocked (прокси) | Windows ✖ | Нет Windows; нет API |
| A32 | Blocked | логика ✔: отказ от более новой схемы, откат неудачной миграции с резервной копией, неожиданный UDF блокирует открытие, 20 циклов без живых процессов (`RepositoryTests.Newer_schema_is_refused_without_changes`, `Failed_migration_rolls_back_and_backs_up_original`, `StorageTests.Effective_udf_check_rejects_other_paths`, `LifecycleTests.Repeated_open_close_cycles_leave_no_live_processes`). Windows ✖: обновление Runtime, рост памяти | Нет Windows |

## Что нужно для перехода в Pass

1. Windows 11 x64 с WebView2 Runtime (Evergreen). Записать build Windows и версию Runtime.
2. Доверенный тестовый сертификат (`mkcert`) и запуск `tests/fixture/fixture_server.py`; Debug-сборка с `PP_FIXTURE_ORIGINS` и `PP_FIXTURE_START`.
3. Пройти A01–A08, A14–A19, A21–A23, A25–A30, A32 по стенду; для каждого записать отдельные подпроверки.
4. Для экспериментальной сборки — тестовый HTTP-прокси с журналом (например, mitmproxy/squid) и внешнее наблюдение трафика для A09–A13, A24, A31.
5. A20 — ручной тест с вашими авторизованными аккаунтами; пароли нигде не сохраняются.
