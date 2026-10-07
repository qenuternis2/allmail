# Приёмка SecureBrowser 0.1.53

Дата: 7 октября 2026. Основание: исходная спецификация v1.1, A01–A32.
[Протокол 0.1.52](acceptance-report-0.1.52.md) сохраняет предыдущие подтверждения,
включая A03/A23/A28 и сетевые границы.

0.1.53 исправляет необработанную ошибку чтения выбранного файла импорта.
Исчезнувший или занятый файл теперь отклоняется через сообщение без изменения
профилей. Проверка размера и чтение выполняются через один дескриптор;
объём чтения ограничен 1 МиБ плюс один байт для обнаружения превышения.
Формат JSON, настройки профилей, публичные интерфейсы и зависимости не изменены.

Регрессионный Windows-прогон
[`1e7b259`](https://github.com/qenuternis2/allmail/actions/runs/37618782486)
дошёл до реального `FileNotFoundException` в production обработчике после
исчезновения выбранного файла. Отказ от неправильных документов и отмена preview
до этого не меняли исходный профиль.
Исправленный полный Windows-прогон
[`014f541`, попытка 2](https://github.com/qenuternis2/allmail/actions/runs/37619147520/attempts/2)
успешен: **653 теста ядра, 108 JS**, Core/Experimental, весь нативный WebView2
и installer install/update/start/keep/remove. SDK **1.0.4258.31**, Runtime
**154.0.4258.62**, runner `Microsoft Windows 10.0.26100`, 4 CPU,
RAM 17 174 360 064 байта. A26 закрыт на этой конфигурации.

[Попытка 1](https://github.com/qenuternis2/allmail/actions/runs/37619147520/attempts/1)
также прошла новый import fixture, но позже завершилась на 10-секундном timeout
локальной страницы `UserAgentHintsBootstrap.VerifyAsync`. Этот код не менялся;
повтор на свежем runner прошёл целиком без увеличения таймаутов, ослабления защиты
или отключения проверок. Причина единичного timeout не установлена; это остаётся
наблюдаемым ограничением устойчивости нативного старта, а не исправленным дефектом.
Локально: **653 теста ядра, 108 JS**, Core/Experimental и native compile,
0 ошибок/предупреждений; анализаторы и workflow YAML прошли.

Новый fixture не автоматизирует системный OpenFileDialog: он вызывает тот же
production путь после выбора файла и взаимодействует с настоящими WPF-диалогами.
Проверяются 28 некорректных документов, отмена и закрытие preview, missing/locked
файлы, подтверждение, свежие UUID/snapshots, отсутствие переноса grants/secrets/UDF,
блокировка неполной сети и сохранение исходного профиля. Никаких реальных сайтов
или пользовательских данных этот fixture не использует. Проверки потери диска
или произвольного сбоя БД при записи не подменяются этой матрицей входных файлов.

A23 сохраняет подтверждение 0.1.52: persisted PID/creation-time record появляется
при запуске Runtime начиная с этой версии. Runtime старых версий без такой записи
не сертифицирован сценарием retained-owner recovery. Повреждённая или неизвестная
запись консервативно блокирует владение и очистку.

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
| A03 | Pass | `HostCrashSmoke`: отдельный процесс WPF-стенда использует production MainWindow/engine/lifecycle; второе открытие отклоняется при живом владельце. После kill только host-процесса захваченные Runtime descendants завершаются сами, lock освобождается; тот же UDF/cookie/LocalStorage читается при повторном открытии A, живой B/PID/metadata/storage неизменны. Задержанный Runtime проверен отдельно в A23 |
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
| A18 | Pass | `ModernUiSmoke`: 105 записей и три реальные среды одновременно, 30 search/selection действий между загруженными/отрисованными страницами, p95 53.59 ms. Первый native frame измеряется отдельно. Четвёртая среда отклонена, три закрыты с ожиданием выхода |
| A19 | Blocked | Runtime detection и установка Evergreen в CI; сценарий отсутствующего Runtime реализован. Изолированный пользовательский тест отсутствия Runtime и обновления с реальными сохранёнными сессиями — не выполнен |
| A20 | Blocked | Реальный аккаунт не предоставлен: sign-in, 2FA, письмо, черновик, вложение, relaunch. Публичная Proton landing page не заменяет этот сценарий |
| A21 | Blocked | Unit concurrent open/close/cancel/restart/delete и stale generation; native tab stale callbacks. Весь сценарий rapid operations с настоящими WPF callbacks — не выполнен |
| A22 | Blocked | Настоящий WPF STA, production engine, explicit environment, popup deferrals, native async permission requests, запреты до первого скрипта. Полный сценарий пользовательских async permission dialogs с перезапуском/закрытием ещё не выполнен |
| A23 | Pass | `HostCrashSmoke` recovery: настоящий независимый native controller удерживает Runtime; production close/reset/delete достигают стандартного timeout 15s, сохраняют SDK exit Task/lock/UDF, запрещают reopen. После kill только host при приостановленном captured browser PID persisted PID/birth record блокирует второй экземпляр и все cleanup intents. Watchdog возобновляет fixture; Runtime/descendants завершаются сами, четыре pending reset/delete безопасно возобновляются; живой B/PID/metadata/LocalStorage неизменны. Unit checks проверяют PID reuse и unknown/corrupt записи |
| A24 | Blocked | Unit pending/applied/revert и свежие secret refs; ошибочный startup check не коммитит pending revision. Все прерывания сетевого переключения на нативном движке не выполнены |
| A25 | Blocked | Core safe deletion, ссылки/junction, interprocess locks и внешние вложения; native Windows Credential Manager/NTFS locked file. Исправлен reset/delete закрытого профиля из второго экземпляра: обязательный ownership lock до любого удаления; unit data/metadata/secrets и native retained-Runtime cleanup подтверждены. Все прерывания reset/delete через пользовательские диалоги ещё не выполнены |
| A26 | Pass | `ImportSettingsSmoke`: настоящий production обработчик после выбора файла, реальные WPF-диалоги отказа/предпросмотра. 28 malformed/oversized документов (включая валидную первую и плохую следующую запись), отмена/закрытие preview, исчезнувший и NTFS-locked файл не меняют metadata/UDF marker/grants исходного профиля. Подтверждение создаёт свежие UUID и snapshots, UDF ленивые, нет credentials/grants/autostart; Unset и неполный Proxy блокируются production lifecycle; sidebar обновляется. Schema/semantic/limits и catalog commit unit tests сохранены |
| A27 | Blocked | Unit dedup/grants/revoke; native real requests/frames; production reset-permissions menu удаляет A choices, сохраняет B/UDF/tab file. Полный пользовательский grant/restart/revoke A/B ещё не выполнен. Browser-native store не используется (`SavesInProfile=false`) |
| A28 | Pass | Native ScriptLocale/timezone/documents/OOP frames/workers; production navigator/Intl/theme/zoom и restore-defaults A/B. `ProfileTabsSmoke` читает фактический HTTP Accept-Language из loopback receiver: A System→de-DE→System с перезапусками, B fr-FR; первый language range соответствует выбранному языку, восстановленный A и неизменённый B точно совпадают с собственными wire baseline. Нативный fallback регионального navigator.language документирован. Часовой пояс явно настраивается по позднему запросу |
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

## Нативный сценарий A23

Тестовый отдельный WPF host использует production MainWindow/engine/lifecycle.
Независимый fixture controller в той же среде удерживает настоящий Runtime
после production CloseAsync. Проверяются штатный timeout **15 секунд**,
RecoveryRequired, сохранение сигнала/lock/UDF и отказ reopen/reset/delete.
Затем останавливается только захваченный owned browser process, завершается
только его host; независимый watchdog гарантирует возобновление fixture Runtime.
Второй экземпляр не должен захватывать UDF или частично удалять его.
После возобновления Runtime завершается сам; pending reset/delete выполняются
только после выхода процесса. Профиль B/PID/metadata/LocalStorage неизменны.
Это настоящий процесс/контроллер/SDK exit event, не подменённая exit Task.

Длительное профилирование managed/SDK/WPF памяти, ручные аккаунты, физические
DPI/мониторы и полная DNS/UDP изоляция остаются в пределах Blocked выше.
