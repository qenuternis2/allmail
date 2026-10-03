# Исправление защиты WebRTC

Дата: 2026-10-03. Исправление внесено в существующий репозиторий `qenuternis2/allmail`.
Приложенное предложение использовано как технический материал; его утверждения о поведении
Runtime не приняты за результаты тестирования. Проверки Windows ниже остаются **Blocked**.

## Что изменено

Раньше приложение всегда добавляло экспериментальный Chromium-флаг ограничения UDP,
не блокировало `RTCPeerConnection`, разрешало запросы устройств по сохранённой политике
и описывало отсутствие кандидатов как отсутствие утечки. Обычная диагностика обращалась
к публичным STUN-серверам. Один флаг и HTTP IP не доказывают защиту от прямого трафика.

Теперь:

- `webRtcPagePolicy`: Block по умолчанию, Allow — явная настройка совместимости.
- `webRtcNetworkPolicy`: RuntimeDefault по умолчанию; RestrictNonProxiedUdpExperimental
  доступен только в экспериментальной сборке. В основной сборке неподдерживаемая настройка
  блокирует открытие, в том числе после импорта. Скрытого перехода на System нет.
- Обе настройки входят в существующую монотонную `ConfigRevision` и неизменяемый снимок
  поколения. Изменение требует полного перезапуска только соответствующего профиля.
- Версионированный встроенный скрипт `Privacy/webrtc-guard.v1.js` делает стандартный,
  legacy и известные transport-конструкторы недоступными через неизменяемые свойства
  глобального объекта и найденные одноимённые свойства его прототипов. Исходные конструкторы,
  функции восстановления и отладочные мосты странице не предоставляются. Скрипт повторяем;
  конфликт дескрипторов вызывает ошибку. Другие признаки браузера не меняются.
- Общая точка регистрации охватывает основной WebView, разрешённые дочерние окна и
  локальную диагностику. `AddScriptToExecuteOnDocumentCreatedAsync` ожидается до target navigation.
  Основной и диагностический контроллер проверяют инъекцию в собственном `about:blank` до
  целевой страницы. Дочерний контроллер остаётся **ненавигированным**, как требует SDK:
  регистрация завершается до присваивания `NewWindow`; deferral завершается на всех путях.
- Ошибка регистрации не позволяет открыть Proton. Поздние readback-проверки документа/фрейма
  обнаруживают доступный конструктор и закрывают поколение. Эти проверки **не предотвращают
  задним числом** ранее выполненный трафик. Закрытие учитывает поколение; запоздавший callback
  не закрывает новый браузер. При неподтверждённом выходе сохраняются RecoveryRequired и lock.
- Camera/Microphone при Block запрещены до проверки сохранённых разрешений, без удаления
  прежних пользовательских решений. Другие разрешения используют прежний единый store.
- Полный набор аргументов собирается один раз из типизированных настроек; proxy-флаг сохраняется,
  IP-handling-флаг добавляется ровно один раз и только по явному выбору. Внешние argument overrides
  в environment/политиках Windows блокируют этот эксперимент консервативно; реестр не изменяется.
- Миграция SQLite v1 → v2 сохраняет профили, permissions, snapshots и UDF; перед миграцией
  используется существующий механизм backup. Старые snapshots и v1-экспорты получают явные defaults.
  Импорт/экспорт и JSON Schema поддерживают новые поля; статусы проверки не переносятся.
- Русский интерфейс разделяет регистрацию блокировки, настройку сети и **непроверенное** покрытие.
  Широких утверждений «полностью отключён» или «утечек нет» нет.
- Обычная диагностика больше не создаёт RTC-соединения и не обращается к STUN/TURN.
  Контролируемый стенд вынесен в `tests/fixture`; нормальные Proton views сохраняют
  `AreHostObjectsAllowed=false`, `IsWebMessageEnabled=false`. Локальная bundled-диагностика
  сохраняет свой отдельный ограниченный канал, принимающий отчёт только от собственного origin.

## Изменённые компоненты

| Файлы / компоненты | Назначение |
| --- | --- |
| `src/ProtonProfiles.Core/Privacy/*` | Встроенный guard v1 и host readback |
| `src/ProtonProfiles.App/Browser/{WebView2Engine,WebView2Session,IBrowserViewHost}.cs`, `MainWindow.xaml.cs` | Инициализация всех контроллеров, child deferrals, stop по поколению и статус |
| `Core/Model/*`, `Core/Network/*`, `Core/Validation/ProfileValidator.cs` | Типизированные политики, capabilities, проверка готовности, аргументы |
| `Core/Permissions/PermissionPolicy.cs`, `Core/Lifecycle/ProfileLifecycleService.cs` | Device denial и безопасное завершение поколения |
| `Core/Persistence/SqliteProfileRepository.cs`, `Core/Interchange/SettingsInterchange.cs`, `schema/profile-settings.schema.json` | Миграция v2 и совместимость обмена |
| `Core/Diagnostics/DiagnosticsReport.cs`, `App/Diagnostics/fingerprint.html`, `App/Dialogs/*` | Отдельные статусы, настройки, исключение публичных RTC probes |
| `tests/ProtonProfiles.Core.Tests/*`, `tests/webrtc-guard.test.mjs` | Регрессии политик, данных, lifecycle и семантики JS |
| `tests/fixture/site/webrtc*`, `tests/fixture/WEBRTC.md` | Управляемый положительный контроль и матрица контекстов |
| `README.md`, `build.ps1`, `.github/workflows/windows.yml` | Исправленные границы защиты и JS-тесты в штатном build/CI |

## Выполненная проверка

Машина: Linux x64; .NET SDK **10.0.100**, runtime **10.0.0**; пакет WebView2 SDK **1.0.4258.31**;
Node.js **24.19.0**. Windows и установленного WebView2 Runtime в этой машине нет.

| Проверка | Результат | Доказательство |
| --- | --- | --- |
| `dotnet build ProtonProfiles.slnx -c Release --no-restore` | Pass: 0 warnings / 0 errors | Release-артефакты всех трёх проектов |
| `dotnet build src/ProtonProfiles.App -c Release -p:ExperimentalProxy=true -o artifacts/build-experimental --no-restore` | Pass: 0 warnings / 0 errors | Артефакт экспериментальной сборки; компиляция, не запуск GUI |
| `dotnet build src/ProtonProfiles.App -c Debug -p:ExperimentalProxy=true -o artifacts/build-fixture --no-restore` | Pass: 0 warnings / 0 errors | Debug-вариант для контролируемого стенда; GUI не запускался |
| `dotnet test tests/ProtonProfiles.Core.Tests -c Release --no-build` | Pass: **190** executed, **190** passed, **0** skipped/failed | `artifacts/test-results/webrtc-core-tests.trx` |
| `node --test tests/webrtc-guard.test.mjs` | Pass: **10** executed, **10** passed, **0** skipped/failed | VM: usable synthetic constructor до guard, immutable descriptors, повторяемость, конфликты, прототипы и readback |
| HTTPS fixture smoke | Pass: **10** функциональных ответов на двух origins | HTTPS с проверкой TLS через локальный тестовый сертификат: HTML/JS/worker и echo endpoint; процесс после теста остановлен |

Проверка VM не моделирует WindowProxy WebView2, timing инъекции, реальные фреймы или сетевые сокеты.
HTTPS smoke проверяет работу сервера и выдачу fixture-файлов, **не RTC-поведение браузера**.
Windows CI был настроен, но запуск workflow в GitHub в этой задаче не выполнялся.

При подготовке релиза выполнен Windows CI: все 190 тестов ядра прошли на Windows 10.0.26100,
.NET SDK 10.0.401 / Runtime 10.0.12. Первый запуск JS-проверок выявил ошибку чтения CRLF
в тестовом parser; в v0.1.2 он нормализует окончания строк. Это не запуск GUI или WebRTC acceptance.

## W01–W18: приёмка в WebView2

Ни один Windows-сценарий не объявлен Pass без запуска в Runtime. Для повторения см.
[`tests/fixture/WEBRTC.md`](tests/fixture/WEBRTC.md). Для каждого недоступного транспорта
пишите отдельный Blocked; результат локальной пары не заменяет удалённые peers.

| ID | Статус | Результат / остающаяся проверка |
| --- | --- | --- |
| W01 | Blocked | Fixture локальной пары с ICE gathering и проверяемой DataChannel-строкой реализован; реальный положительный контроль не запускался |
| W02 | Blocked | Guard/дескрипторы проверены в VM; ранний скрипт реального документа требует Windows |
| W03 | Blocked | Reload, оба origins и relaunch с сохранённой сессией — Windows |
| W04 | Blocked | Fixture покрывает fresh realm, about:blank, srcdoc, blob и cross-origin; timing и bypass требуют Runtime |
| W05 | Blocked | Код соблюдает un-navigated NewWindow и deferrals; earliest child script / blank child access не выполнены |
| W06 | Blocked | Dedicated/shared/service worker inventory реализован; exposed usable RTC API означает Fail полного покрытия, не автоматический Pass |
| W07 | Blocked | Unit denial сохранённых grants прошёл; реальный DataChannel и device permission path требуют Windows и оборудования |
| W08 | Blocked | Unit изоляции settings/generations прошёл; два реальных proxies и profiles не проверены |
| W09 | Blocked | Типизированные аргументы проверены; отдельное сетевое действие флага при Allow не наблюдалось |
| W10 | Blocked | Нет proxy infrastructure и сетевого захвата для no-proxy / unavailable / auth failure / mid-session loss |
| W11 | Blocked | STUN, TURN/UDP, TURN/TCP, TURN/TLS, IPv4, IPv6, local peer должны иметь успешные положительные контроли; удалённый two-peer signaling не реализован в локальном fixture |
| W12 | Blocked | Unit immutable snapshot, pending revision, restart одного профиля и завершение старого fake process прошли; реальный Runtime не проверен |
| W13 | Blocked | Unit позднего callback, exit timeout и сохранения lock прошёл; реальные cancellation/child failure/exit races не проверены |
| W14 | Blocked | VM-конфликты и unsupported import проверены; registration/registry/Runtime fault injection требует Windows |
| W15 | Blocked | Cold start, persisted worker activity и остановленный proxy требуют внешнего наблюдения с process creation |
| W16 | Blocked | Actual Runtime version записывается в session; update/guard-version regression на Windows не выполнен |
| W17 | Blocked | Windows и разрешённый существующий Proton-аккаунт недоступны; sign-in/2FA/mail workflows не выполнялись |
| W18 | Blocked | Unit migration/import/export/schema/regressions прошли; реальная Windows-сессия и полный диагностический сценарий не проверены |

## Границы защиты и отдельная сетевая граница

Это блокировка известных **API страниц**, а не удаление WebRTC из движка. Document-created injection
не считается универсальной защитой workers, новых Runtime API, мгновенного доступа к свежим realms
или активности сохранённых service workers. Наличие незакрытого обхода в Windows-тестах означает Fail
полного page-blocking objective; позднее закрытие не исправляет уже совершённую передачу.

Экспериментальный флаг не запрещает весь TCP/TURN и не является kill switch. Строгая цель
«никакого трафика вне назначенного proxy» **Blocked: отдельная сетевая граница отсутствует**.
WFP/firewall правила не устанавливались. Scoped-проект такой границы должен включать:

1. Отдельную сетевую идентичность/изолированную среду каждого профиля и его Runtime-процессов;
   общий путь `msedgewebview2.exe` недостаточен и затронет другие приложения.
2. Default-deny с момента создания процессов, включая дочерние network owners, IPv4/IPv6 и races;
   только проверенный proxy endpoint разрешён. DNS/resolution, Runtime updates и signaling
   учитываются отдельно, без незаметного direct fallback.
3. Проверку отказа proxy, смены адресов/Runtime path, restart и cold persisted activity;
   отсутствие доступного proxy сохраняет запрет. TURN/TCP/TLS через proxy всё ещё возможен:
   это route enforcement, а не нативное отключение WebRTC.
4. Независимые положительные контроли и сетевое наблюдение до объявления enforcement проверенным.

Этот компонент требует отдельного Windows deployment/design task. Расширения, registry browser policies,
неподтверждённые switches, бинарные патчи Runtime, отключение sandbox/TLS и правки UDF preferences
в исправление не входят.

## Откат

Для диагностики совместимости можно явно выбрать Allow в одном синтетическом профиле и выполнить
полный перезапуск; это снимает блокировку API. Отключение экспериментального сетевого режима также
требует перезапуска. Для возврата исходного приложения после миграции v2 сначала закройте все профили
и подтвердите выход их процессов, сохраните актуальную metadata DB/UDF, затем восстановите
предмиграционный backup metadata v1. Старое приложение откажется читать схему v2. Изменения metadata
после backup в него не входят; не удаляйте браузерные данные и не редактируйте работающую базу.

## Проверенные контракты SDK

Использованы API существующего пакета; framework и зависимости не обновлялись.
Контракт `NewWindow` дополнительно проверен по XML-документации SDK 1.0.4258.31:
тот же environment/profile, отсутствие навигации, завершённая регистрация script до присваивания.

- [AddScriptToExecuteOnDocumentCreatedAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.addscripttoexecuteondocumentcreatedasync)
- [NewWindow](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2newwindowrequestedeventargs.newwindow)
- [WebView2 browser flags](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)
- [RFC 8828: IP handling и ограничения маршрутов](https://www.rfc-editor.org/rfc/rfc8828.html)
