# Приёмка SecureBrowser 0.1.51

Дата: 7 октября 2026. Основание: исходная спецификация v1.1, A01–A32.
Отчёт 0.1.0 сохранён отдельно: [исторический протокол](acceptance-report.md).
Предыдущий Windows CI 0.1.50: [успешный полный прогон](https://github.com/qenuternis2/allmail/actions/runs/37591157537),
ревизия `76ba4e319999b8f67fb5324f9be9eb09ff562726`. Пройдены 646 тестов ядра,
108 JavaScript-тестов, сборки Core/Experimental Proxy, нативный WebView2 и
установка/повторная установка/запуск/удаление с сохранением и очисткой данных.
SDK **1.0.4258.31**, Runtime **154.0.4258.62**, Windows runner сообщает
`Microsoft Windows 10.0.26100`, 4 CPU, доступная RAM 17 174 360 064 байта.
105 записей / 30 search-selection действий: **p95 194.59 ms**. Это измерение
на runner, не проверка физического Windows 11 с разными DPI/мониторами.
Локальные сборки/нативный стенд: 0 ошибок и предупреждений; анализаторы и синтаксис
PowerShell без ошибок. Весь набор повторяется перед публикацией выпуска.

Принятые позднее изменения ТЗ: SecureBrowser вместо Proton Profiles, любые
HTTP/HTTPS сайты, вкладки/группы, настройки защиты и их исключения, локальный
GeoIP и автоматический часовой пояс. Поэтому Proton allowlist, запрет всей
JavaScript-защиты и только системный часовой пояс не восстанавливаются.
Баннер удалён по запросу пользователя; статус экспериментального сетевого
адаптера сохранён в документации и свойствах возможностей.

Родительский критерий Pass допустим только после всех его подпунктов.
Blocked означает, что остаётся непроверенный обязательный подпункт, даже если
остальные автоматические проверки проходят. Названия файлов ниже — точные
места проверки; unit-double не выдаётся за настоящий процесс браузера.

| Критерий | Статус | Выполненные проверки / оставшийся подпункт |
| --- | --- | --- |
| A01 | Pass | `ProfileTabsSmoke`: persistent cookies (Max-Age)/LocalStorage/IndexedDB/CacheStorage A/B, положительный контроль общих вкладок, отдельный перезапуск каждого профиля и повторное чтение обоих |
| A02 | Pass | Service Worker cache A/B и сохранение после перезапуска; BroadcastChannel: сообщение внутри A получено, в B отсутствует. Проверяется RuntimeDefault; строгая защита намеренно блокирует SW |
| A03 | Blocked | Unit interprocess locks; native второй lifecycle отклоняет занятый UDF. Осталось аварийное завершение отдельного процесса приложения с оставшимся Runtime |
| A04 | Blocked | Unit reset/delete A сохраняет B; native изоляция хранилищ. Выход из реального Proton и отзыв серверной сессии — вручную |
| A05 | Pass | `ProfileTabsSmoke`: popup/дочерние вкладки в той же среде, общий профиль, защита до первого скрипта |
| A06 | Blocked | Native document/frame/worker UA/Client Hints; ограничения SharedWorker при разрешённом исключении документированы. Все варианты Custom UA в persisted worker не подтверждены |
| A07 | Pass | Unit Default/Custom и restart; native production lifecycle: Custom→Default восстанавливает текущий native UA, persistent cookie/LocalStorage сохраняются |
| A08 | Pass | Unit per-profile settings/restart; native production lifecycle: язык de-DE с нативным fallback de, Intl de-DE, Dark/Light и zoom 1.25 A при неизменённом B, restart и restore defaults |
| A09 | Blocked | Native IPv4/IPv6 HTTP, unresolved hostname через proxy, HTTPS/WSS CONNECT; Basic-auth production adapter. Две одновременно работающие реальные внешние прокси-сети не проверены |
| A10 | Blocked | Stopped-proxy отрицательные HTTP/HTTPS/WSS проверки и нулевые direct receiver counts. Полный захват всех процессов/UDP/DNS с активными workers — не выполнен |
| A11 | Blocked | Unit завершение старой среды/revision/новый secret ref; native повторное использование UDF после завершения. Замена живого маршрута с внешним сетевым наблюдением — не выполнена |
| A12 | Pass | `ProfileTabsSmoke`: первый запрос после GeoIP, четыре параллельных 407, чужой 401 без proxy secret, ограниченный повтор неверного пароля, новый запрос после отказа |
| A13 | Blocked | HTTP/fetch/frames/workers/CONNECT, WSS и вложения проверяются отдельными fixtures. Разрешение fake hostname на стороне proxy проверено. OS DNS/UDP/process-wide наблюдение не выполнено; список протоколов ниже |
| A14 | Blocked | Native deny/frame requests и fake camera/mic positive control; независимые списки загрузок A/B, файлы не запускаются автоматически. Полный grant/revoke через пользовательские диалоги A/B ещё не выполнен |
| A15 | Pass | Native точечный crash browser PID A, освобождение контроллеров, сохранённый UDF, B остаётся Open, повторное открытие A |
| A16 | Pass (логика экспорта) | `InterchangeTests`, diagnostics/redaction tests: без secret/UUID/UDF/grants/session transfer; свежие идентификаторы и блокировка неполной сети |
| A17 | Pass (календарная логика) | `ReminderTests`: календарные месяцы, timezone, snooze/confirmation; локальная отметка не означает проверенную серверную активность |
| A18 | Blocked до Windows CI 0.1.51 | `ModernUiSmoke`: 105 записей, 30 search/selection действий, p95; native lifecycle три среды, четвёртая отклонена, закрытые browser PID исчезли; ModernUiSmoke теперь открывает три реальные среды в каталоге 105 записей, переключает UI между ними и проверяет отказ четвёртой; ожидается CI этой версии |
| A19 | Blocked | Runtime detection и установка Evergreen в CI; сценарий отсутствующего Runtime реализован. Изолированный пользовательский тест отсутствия Runtime и обновления с реальными сохранёнными сессиями — не выполнен |
| A20 | Blocked | Реальный аккаунт не предоставлен: sign-in, 2FA, письмо, черновик, вложение, relaunch. Публичная Proton landing page не заменяет этот сценарий |
| A21 | Blocked | Unit concurrent open/close/cancel/restart/delete и stale generation; native tab stale callbacks. Весь сценарий rapid operations с настоящими WPF callbacks — не выполнен |
| A22 | Blocked | Настоящий WPF STA, production engine, explicit environment, popup deferrals, native async permission requests, запреты до первого скрипта. Полный сценарий пользовательских async permission dialogs с перезапуском/закрытием ещё не выполнен |
| A23 | Blocked | Unit timeout→RecoveryRequired, retained exit signal/lock и отсутствие массового kill; native обычный/crash exit. Host crash с живым Runtime и реальной задержкой выхода — не выполнен |
| A24 | Blocked | Unit pending/applied/revert и свежие secret refs; ошибочный startup check не коммитит pending revision. Все прерывания сетевого переключения на нативном движке не выполнены |
| A25 | Blocked | Core safe deletion, ссылки/junction, interprocess locks и внешние вложения; native Windows Credential Manager/NTFS locked file: Pending сохраняет секреты, повторная очистка удаляет только A. Все прерывания reset/delete через пользовательский UI ещё не выполнены |
| A26 | Blocked | Все schema/semantic/size/atomic import tests; реальные XAML формы настроек. Диалог предварительного просмотра импорта для полного набора плохих файлов ещё не автоматизирован |
| A27 | Blocked | Unit dedup/grants/revoke; native real requests/frames; production reset-permissions menu удаляет A choices, сохраняет B/UDF/tab file. Полный пользовательский grant/restart/revoke A/B ещё не выполнен. Browser-native store не используется (`SavesInProfile=false`) |
| A28 | Blocked | Native ScriptLocale/timezone/documents/OOP frames/workers; native navigator/Intl/theme/zoom restore-defaults A/B выполнен, wire Accept-Language ещё не проверен. Часовой пояс теперь явно настраивается по позднему запросу |
| A29 | Blocked | Production blob download и точный payload; real HTTP pause/resume/cancel/closed-tab ownership; unsafe URI policy unit/native. Запуск внешней ссылки системным браузером — вручную; обычные HTTP сайты теперь открываются во вкладках |
| A30 | Blocked | Native четвёртый профиль не открывается и не вытесняет три существующих; unit capacity includes Starting/pinned. UI выбор при несохранённой форме и активной загрузке — вручную |
| A31 | Blocked | Startup check отключает disk cache и bypasses сохранённый SW до признания proxy revision применённой. Stopped-proxy check проверяется native. Непрерывное наблюдение от старта всех процессов с ранее сохранённым SW не выполнено |
| A32 | Blocked | Unit migration rollback/newer schema/UDF override; native legacy migration и 20 настоящих open/close циклов с выходом PID/дочерних процессов и CSV aggregate host/Runtime памяти, managed bytes и weak references на закрытые сессии. Runtime update + реальные customer sessions остаются непроверенными |

## Сетевые границы

| Путь | Проверка | Ограничение |
| --- | --- | --- |
| HTTP IPv4/IPv6 loopback | Proxy receiver получает absolute URL; direct receiver count=0; отказ при остановке proxy | Локальный fixture, не произвольная пользовательская сеть |
| DNS hostname | Не разрешаемое `.invalid` имя успешно обслуживается только proxy | Не доказывает отсутствие всех OS DNS-запросов |
| HTTPS/WSS | CONNECT и echo; отрицательный stopped-proxy; отдельный доверенный fixture TLS | Не полная firewall-сертификация |
| fetch/iframe/worker | Headers/privacy/contexts/GeoIP native fixtures | Не непрерывный process-wide захват каждого протокола |
| blob/HTTP attachments | Производственный DownloadStarting, payload, pause/resume/cancel; вкладка закрыта — transfer продолжается | Закрытие профиля завершает его transfers |
| UDP/WebRTC | Page/native restrictions и readback | Физический UDP-трафик и все служебные соединения Runtime не сертифицированы |

HTTP 401/403/429 страницы могут требовать действий пользователя и не являются
ошибкой соединения. Сессионные cookies без Expires/Max-Age имеют штатный срок жизни Runtime; тест
перезапуска явно задаёт persistent cookies, не продлевает чужие истёкшие cookies.
Proxy startup check проверяет получение HTTP-ответа с сайта
профиля, не использует новый внешний IP-сервис и не гарантирует, что весь трафик
Runtime изолирован. Прокси по-прежнему задаётся `--proxy-server`; документированного
production API для этого в выбранном WebView2 нет. Смена движка не согласована.

## Ручная часть

На Windows 11 проверить масштаб 100/125/150/200%, перенос между мониторами,
keyboard/focus во всех диалогах, реальные proxy failures и routing capture.
A20 выполнять на существующем разрешённом аккаунте: sign-in/2FA → открыть письмо →
черновик → скачать blob/HTTP вложение → закрыть и запустить профиль снова.
Проверить, что профиль B не изменился. Никакие секреты/содержимое писем не включать
в отчёт. Не создавать фиктивный Pass без такого прогона.

Установщик unsigned до предоставления настоящего доверенного сертификата.
Реализован optional signing и CI install/update/uninstall smoke; подпись и
production proxy API остаются внешними ограничениями, а не скрытыми готовыми функциями.

## Нативный цикл памяти

В указанном прогоне 20 циклов: открытая host/Runtime working set
602 419 200 → 548 274 176 байт; после закрытия host working set
259 862 528 → 211 361 792 байта. Во всех циклах закрытых Runtime процессов,
live environments и удерживаемых закрытых сессий (weak references) — **0**.
Managed bytes: 8 201 544 → 9 516 520, с промежуточным снижением; отсутствие
любого роста managed heap не заявляется. Отладочное поле стенда, удерживавшее
последний Core, очищено; проверка запрещает накопление закрытых сессий.
Полный `lifecycle-memory.csv` входит в CI artifacts. Для вывода об отсутствии
любой managed/SDK/WPF утечки нужен более длинный профиль памяти.

0.1.51: результат Test-Installer выводится в success stream, поэтому Tee-Object
создаёт installer.log для ZIP. Установка/сохранение хеша metadata/внешнего
вложения уже проверены в failed packaging run 0.1.50; публикация не выполнялась.
