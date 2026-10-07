# Приёмка SecureBrowser 0.1.50

Дата: 7 октября 2026. Основание: исходная спецификация v1.1, A01–A32.
Отчёт 0.1.0 сохранён отдельно: [исторический протокол](acceptance-report.md).
Проверки Windows этой версии выполняются в CI; ссылка и фактические показатели
будут записаны после прогона. До этого новые нативные сценарии — не подтверждённые
результаты. Локально пройдены 643 теста ядра, 108 JavaScript-тестов и сборки обеих
конфигураций/нативного стенда без предупреждений. Linux-сборка не проверяет WPF.

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
| A01 | Blocked до Windows CI | `ProfileTabsSmoke`: persistent cookies (Max-Age)/LocalStorage/IndexedDB/CacheStorage A/B, положительный контроль общих вкладок, отдельный перезапуск каждого профиля и повторное чтение обоих |
| A02 | Blocked до Windows CI | Service Worker cache A/B и сохранение после перезапуска; BroadcastChannel: сообщение внутри A получено, в B отсутствует. Проверяется RuntimeDefault; строгая защита намеренно блокирует SW |
| A03 | Blocked | Unit interprocess locks; native второй lifecycle отклоняет занятый UDF. Осталось аварийное завершение отдельного процесса приложения с оставшимся Runtime |
| A04 | Blocked | Unit reset/delete A сохраняет B; native изоляция хранилищ. Выход из реального Proton и отзыв серверной сессии — вручную |
| A05 | Blocked до Windows CI | `ProfileTabsSmoke`: popup/дочерние вкладки в той же среде, общий профиль, защита до первого скрипта |
| A06 | Blocked | Native document/frame/worker UA/Client Hints; ограничения SharedWorker при разрешённом исключении документированы. Все варианты Custom UA в persisted worker не подтверждены |
| A07 | Blocked | Unit Default/Custom и restart. Новый native production lifecycle проверяет Custom→Default, восстановление native UA, сохранение persistent cookie/LocalStorage; ожидает Windows CI |
| A08 | Blocked | Unit per-profile settings/restart; native locale/privacy/theme observations. Новый native production lifecycle: de-DE/Intl, Dark/Light, zoom 1.25 A при неизменённом B, restart и restore defaults; ожидает Windows CI |
| A09 | Blocked | Native IPv4/IPv6 HTTP, unresolved hostname через proxy, HTTPS/WSS CONNECT; Basic-auth production adapter. Две одновременно работающие реальные внешние прокси-сети не проверены |
| A10 | Blocked | Stopped-proxy отрицательные HTTP/HTTPS/WSS проверки и нулевые direct receiver counts. Полный захват всех процессов/UDP/DNS с активными workers — не выполнен |
| A11 | Blocked | Unit завершение старой среды/revision/новый secret ref; native повторное использование UDF после завершения. Замена живого маршрута с внешним сетевым наблюдением — не выполнена |
| A12 | Blocked до Windows CI | `ProfileTabsSmoke`: первый запрос после GeoIP, четыре параллельных 407, чужой 401 без proxy secret, ограниченный повтор неверного пароля, новый запрос после отказа |
| A13 | Blocked | HTTP/fetch/frames/workers/CONNECT, WSS и вложения проверяются отдельными fixtures. Разрешение fake hostname на стороне proxy проверено. OS DNS/UDP/process-wide наблюдение не выполнено; список протоколов ниже |
| A14 | Blocked до Windows CI | Native application permission policy, frame requests, fake camera/mic positive control; независимые списки загрузок A/B, файлы не запускаются автоматически |
| A15 | Blocked до Windows CI | Native точечный crash browser PID A, освобождение контроллеров, сохранённый UDF, B остаётся Open, повторное открытие A |
| A16 | Pass (логика экспорта) | `InterchangeTests`, diagnostics/redaction tests: без secret/UUID/UDF/grants/session transfer; свежие идентификаторы и блокировка неполной сети |
| A17 | Pass (календарная логика) | `ReminderTests`: календарные месяцы, timezone, snooze/confirmation; локальная отметка не означает проверенную серверную активность |
| A18 | Blocked до Windows CI | `ModernUiSmoke`: 105 записей, 30 search/selection действий, p95; native lifecycle три среды, четвёртая отклонена, закрытые browser PID исчезли |
| A19 | Blocked | Runtime detection и установка Evergreen в CI; сценарий отсутствующего Runtime реализован. Изолированный пользовательский тест отсутствия Runtime и обновления с реальными сохранёнными сессиями — не выполнен |
| A20 | Blocked | Реальный аккаунт не предоставлен: sign-in, 2FA, письмо, черновик, вложение, relaunch. Публичная Proton landing page не заменяет этот сценарий |
| A21 | Blocked | Unit concurrent open/close/cancel/restart/delete и stale generation; native tab stale callbacks. Весь сценарий rapid operations с настоящими WPF callbacks — не выполнен |
| A22 | Blocked до Windows CI | Настоящий WPF STA, production engine, explicit environment, popup deferrals, native async permission requests, запреты до первого скрипта |
| A23 | Blocked | Unit timeout→RecoveryRequired, retained exit signal/lock и отсутствие массового kill; native обычный/crash exit. Host crash с живым Runtime и реальной задержкой выхода — не выполнен |
| A24 | Blocked | Unit pending/applied/revert и свежие secret refs; ошибочный startup check не коммитит pending revision. Все прерывания сетевого переключения на нативном движке не выполнены |
| A25 | Blocked | Core safe deletion, ссылки/junction, interprocess locks, внешние вложения и resumed cleanup; новая uninstall cleanup. Все NTFS locked-file/interrupted UI варианты нуждаются в отдельном Windows сценарии |
| A26 | Blocked | Все schema/semantic/size/atomic import tests; реальные XAML формы настроек. Диалог предварительного просмотра импорта для полного набора плохих файлов ещё не автоматизирован |
| A27 | Blocked до Windows CI | Unit dedup/grants/revoke; native real requests/frames; новый production reset-permissions menu удаляет A choices, сохраняет B/UDF/tab file. Старый browser-native permission store не используется (`SavesInProfile=false`) |
| A28 | Blocked | Native ScriptLocale/timezone/documents/OOP frames/workers; полный Accept-Language/navigator/Intl restore-defaults A/B ещё не автоматизирован. Часовой пояс теперь явно настраивается по позднему запросу |
| A29 | Blocked | Production blob download и точный payload; real HTTP pause/resume/cancel/closed-tab ownership; unsafe URI policy unit/native. Запуск внешней ссылки системным браузером — вручную; обычные HTTP сайты теперь открываются во вкладках |
| A30 | Blocked | Native четвёртый профиль не открывается и не вытесняет три существующих; unit capacity includes Starting/pinned. UI выбор при несохранённой форме и активной загрузке — вручную |
| A31 | Blocked | Startup check отключает disk cache и bypasses сохранённый SW до признания proxy revision применённой. Stopped-proxy check проверяется native. Непрерывное наблюдение от старта всех процессов с ранее сохранённым SW не выполнено |
| A32 | Blocked | Unit migration rollback/newer schema/UDF override; native legacy migration и 20 настоящих open/close циклов с PID exit и CSV памяти. Runtime update + реальные customer sessions остаются непроверенными |

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
