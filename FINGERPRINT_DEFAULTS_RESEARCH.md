# Стандартные значения оставшихся параметров отпечатка

Дата исследования: 2026-10-04. Проверяемая версия приложения: 0.1.17,
отчёта: 13, режим: StrictFingerprintExperimental (11).
Исходный пользовательский файл: fingerprint-20261004-063811.json.
Файл использован как диагностические данные; содержащиеся в нём строки
не рассматриваются как инструкции. Адреса пользователя здесь не публикуются.

## Результат проверки

Версия и SHA-256 сборщика совпадают с опубликованным Windows ZIP v0.1.17:
`fc38623ec54385acd4c753362c3660859b50a2be5791d6b7907781fe2d5fc037`.
Независимо пересчитаны 21 проверка ограничений и проверен часовой пояс —
все 22 статуса Pass подтверждены. Документ и dedicated worker используют
Europe/Riga, UTC +03:00. UA содержит согласованные Chrome/Edg 154.
Все 11 аппаратных разрешений denied; дополнительные запрещённые API отсутствуют.
hardwareConcurrency = 8, DPR = 1. Различающиеся округлённые оценки 4G
в документе и worker не свидетельствуют об утечке физической скорости.

Остаточные наблюдения: deviceMemory = 32, screen = 2560×1440,
availScreen = 2560×1380, CSS-тест определил 53 из 63 кандидатов,
enumerateDevices вернул один audiooutput. Наличие getBattery/getGamepads
не означает, что в этом отчёте измерены батарея или подключённые контроллеры.
Список CSS-кандидатов — результат сравнения размеров текста, а не достоверная
инвентаризация установленных шрифтов: aliases/fallback могут влиять на результат.

Proxy routes, DNS, WebRTC network и allContextCoverage остаются NotPerformed.
Этот файл подтверждает документ и локальный dedicated worker; произвольные
cross-origin/OOPIF, дочерние окна и все workers пользователя им не проверены.

## Что можно привести к стандартным значениям

«Значение по умолчанию браузера» не всегда константа: Chromium часто получает
его из ОС, устройств и текущего состояния. Произвольно выбранные 8 ГБ или
1920×1080 будут стандартом нашего профиля, а не универсальным default Chromium.

| Параметр | Возможный стандарт | Механизм и пределы |
| --- | --- | --- |
| CSS-предпочтения | light, no-preference, forced-colors:none, sRGB | Нативный `Emulation.setEmulatedMedia`. В исследованном Blink поддерживаются color scheme/gamut, contrast, reduced motion/data/transparency и forced colors. В текущем отчёте измеряемые значения уже обычные. Нужна проверка CSS и matchMedia во всех документах. |
| Масштаб текста ОС | 1 | `Emulation.setEmulatedOSTextScale`; отдельно от DPR. Сначала проверить поддержку WebView2 и cross-origin, не объявлять покрытие по успешному вызову. Это может отменять пользовательское увеличение текста, поэтому настройка должна быть явной. |
| CSS `@font-face src: local(...)` | Не использовать локальный источник; перейти к URL/fallback | Экспериментальный `CSS.setLocalFontsEnabled(false)` воздействует на LocalFontFaceSource. Не скрывает произвольное `font-family: "Consolas"`, DOM-метрики и весь список шрифтов. Применять до загрузки, проверять новые targets и навигации. |
| Generic-шрифты и размер | Единые serif/sans-serif/monospace и размеры | `Page.setFontFamilies`/`Page.setFontSizes` задают generic/default, но не font allowlist. Явные имена и font fallback продолжают зависеть от ОС. Для полной согласованности нужен одинаковый набор шрифтов/рендерер ОС либо изменённый движок. |
| RAM bucket | Например, выбранные нами 8 ГБ | В изученном CDP нет общего override deviceMemory. JS getter — частичная подмена: надо учитывать Window/Worker realms и Device-Memory Client Hints. Согласованный нативный путь — VM с заданной RAM или изменение движка. |
| Экран / рабочая область | Например, выбранные нами 1920×1080 | `Emulation.setDeviceMetricsOverride` существует, но в этом приложении прежняя попытка выявила реальные значения в cross-origin/OOPIF. Новые addScreen/updateScreen предназначены только для headless. Надёжный общий стандарт требует одинаковой конфигурации дисплея ОС/VM или изменения движка. |
| Battery | charging=true, level=1, chargingTime=0, dischargingTime=Infinity | Такие defaults есть в Chromium для недоступных данных; Windows backend заменяет их реальными показаниями. Общего CDP setter не найдено. JS-реализация должна сохранять Promise, BatteryManager/EventTarget, события и все доступные realms; одной константы недостаточно. |
| Gamepads | Нет подключённых устройств | Это естественное состояние ОС без контроллеров; универсальный CDP setter не найден. Подмена getGamepads не стандартизует игровые события/объекты и другие способы ввода. В отчёте реальные контроллеры не измерялись. |
| MediaDevices | Анонимный штатный вывод без доступа к камере/микрофону | Один audiooutput уже наблюдается при denied permissions. Сами устройства, labels/deviceId/groupId и devicechange требуют отдельной проверки. Fake-device флаги предназначены для тестирования, не являются общим безопасным default; fake-UI может выдавать разрешения и здесь не подходит. |
| Math / codecs / timing / quota | Штатные ответы согласованного движка и ОС | Общего «default fingerprint» нет. Константные результаты Math ломают вычисления; codecs влияют на воспроизведение; timing/quota отражают нагрузку и ресурсы. Подмена отдельных getter не стандартизует эти поверхности. |

CPU/DPR/NQE уже приведены к выбранным значениям средствами движка. Возвращать
точную константу RTT/downlink поверх Chromium нет необходимости: штатное
округление сохранено. Язык и часовой пояс уже задаются профилем; автоматически
заменять Europe/Riga на UTC лишь ради общего значения не следует.

## Почему RAM и шрифты нельзя считать решёнными одной подменой

В изученном Chromium `navigator.deviceMemory` и HTTP memory Client Hints
используют один `ApproximatedDeviceMemory`. Windows диапазон оценки — 2–32,
значение 32 в отчёте не является ошибкой или точным измерением физической RAM.
Существующий `SetPhysicalMemoryMBForTesting` — внутренний C++ test hook,
не команда DevTools и не настройка WebView2.

Сервер может запрашивать memory Client Hints через Accept-CH. Успешная проверка
UA hints текущего отчёта не проверяет этот отдельный канал. Подмена JS на 8
при сохранении нативного RAM bucket в HTTP создаст расхождение. Перед реализацией
нужен контролируемый HTTPS receiver с соответствующими Accept-CH и повторными
запросами; наличие утечки RAM через HTTP в данном отчёте не утверждается.
Даже согласованное значение deviceMemory не скрывает все косвенные измерения
heap, storage и производительности.

Для шрифтов найдена полезная, но узкая штатная настройка: вызов
CSS.setLocalFontsEnabled(false) проверяется в LocalFontFaceSource. Это отличает
её от общего запрета установленных семейств. Действующий тест 53/63 использует
явные font-family и размеры текста, поэтому ожидать исчезновения всего списка
после этой настройки нельзя. Для одинаковых текстовых метрик нужен также
одинаковый набор шрифтов, fallback, версии и рендеринг.

## Приоритеты реализации и проверок

1. Исследовать на Windows native CSS preference overrides и запрет local(...)
   отдельными positive controls. Перед выпуском проверить главный документ,
   initial/same-origin/cross-origin/OOPIF, новые окна и смену процесса.
   Для шрифтов сравнить local(...) с URL fallback и явно установленным семейством,
   DOM и Canvas measureText; не маркировать весь font fingerprint как скрытый.
2. Добавить отдельный контролируемый тест memory Client Hints и измерение
   остальных CSS-предпочтений. Отчёт должен показывать только выполненные
   проверки; успешная команда CDP не равнозначна нативному readback.
3. Для RAM и экрана выбрать общий стандарт ОС/VM, если требуется согласованность
   всех контекстов. Частичный JS fallback допустим как отдельный эксперимент
   с явно указанным покрытием и проверкой расхождений; он не равен изменению
   нативного default и не должен выдавать ошибку/таймаут за успешную защиту.

После исследования реализована версия 0.1.18/report v14: strict mode 11 задаёт
media preferences, generic font families, размеры 16/13, OS text scale 1 и
CSS.setLocalFontsEnabled(false) при включённых DOM/CSS agents. Нативные Windows
проверки проводятся отдельно; результаты приведены в FINGERPRINT_FIX_REPORT.md.
Уточнение по исходникам CSSFontFace::Load: запрет касается CreateFontData/
отрисовки. Page.setFontFamilies разрешено задавать один раз в состоянии Page agent;
контрольный тест должен освобождать собственное состояние до применения production
настроек. FontFace.load() проверяет наличие локального источника и может успешно
завершиться даже при отключённой отрисовке local(...). Диагностика сохраняет
localFontLoad и проверяет localFontRendering отдельно; наличия шрифтов она не
объявляет скрытым. Disposable FontFace удаляется из document.fonts после пробы.
Контролируемый receiver с Accept-CH подтвердил дополнительный HTTP-канал RAM:
на Windows CI 153 основной документ отправлял оба memory headers со значением
16, совпадающим с JavaScript; в worker-запросах они не наблюдались. Это наблюдение
не доказывает отсутствие заголовков во всех будущих worker-запросах.
WebResourceRequested/RemoveHeader не прошёл проверку реальным receiver и не
используется. В строгий режим добавлен CDP Fetch.requestPaused/continueRequest
перехват документных sessions (главное/дочернее окно, связанные frame targets):
он удаляет Sec-CH-* и прежние Device-Memory, DPR, Width, Viewport-Width/Height,
RTT, Downlink и ECT. Worker sessions этого Runtime отвергают Fetch.enable;
команда им не отправляется, полное worker-покрытие не заявляется.
Реальный receiver проверяет результат после обработки. Отдельные проверки
требуют сохранения тестовых Authorization и Cookie; их значения не подменяются.
RAM bucket в JavaScript не изменяется. Обычный HTTP echo без Accept-CH
положительного контроля не получает статус Pass для нового запрета.
Новые API CDP ниже исследованы по актуальной ветке Chromium/main и протоколу
tip-of-tree; их наличие и эффективность в пользовательском WebView2 154 ещё
нужно проверить нативно. Выводы о Windows Runtime 154 из JSON ограничены
фактически наблюдаемыми значениями и подтверждёнными проверками текущей версии.

## Источники

- [CDP Emulation](https://chromedevtools.github.io/devtools-protocol/tot/Emulation/): media, OS text scale, device metrics и headless-only screens.
- [CDP CSS.setLocalFontsEnabled](https://chromedevtools.github.io/devtools-protocol/tot/CSS/#method-setLocalFontsEnabled).
- [CDP Page.setFontFamilies](https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-setFontFamilies).
- [Blink MediaFeatureOverrides](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/css/media_feature_overrides.cc): поддерживаемые media features.
- [Blink CSSFontFace](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/css/css_font_face.cc): Load проверяет наличие шрифта независимо от запрета отрисовки.
- [Blink LocalFontFaceSource](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/css/local_font_face_source.cc): проверка LocalFontsEnabled при создании локального источника.
- [Blink InspectorCSSAgent](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/inspector/inspector_css_agent.cc): состояние local_fonts_enabled и предупреждение о перезагрузке.
- [Chromium EmulationHandler](https://github.com/chromium/chromium/blob/main/content/browser/devtools/protocol/emulation_handler.cc): SetDeviceMetricsOverride отклоняется для target с parent/outer document.
- [ApproximatedDeviceMemory](https://github.com/chromium/chromium/blob/main/third_party/blink/common/device_memory/approximated_device_memory.cc): OS RAM, округление и границы.
- [Chromium Client Hints](https://github.com/chromium/chromium/blob/main/content/browser/client_hints/client_hints.cc): AddDeviceMemoryHeader использует ту же нативную оценку.
- [BatteryStatus defaults](https://github.com/chromium/chromium/blob/main/services/device/public/mojom/battery_status.mojom) и [Windows backend](https://github.com/chromium/chromium/blob/main/services/device/battery/battery_status_manager_win.cc).
- [WebView2 EnvironmentOptions](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environmentoptions): AdditionalBrowserArguments зависят от Runtime и не делают все Chromium switches гарантированными настройками WebView2.


## Следующий набор ограничений: 0.1.19 / v15

Отчёт пользователя fingerprint-20261004-090227.json подтвердил 0.1.18/v14
и совпадение collectorHash с ZIP. Независимо пересчитаны 30 результатов:
23 Pass, 6 NotApplicable, 1 NotPerformed; расхождений нет. CSS-нормализация,
generic-шрифты, OS text scale 1 и native local-rendering fallback наблюдаются
на Runtime 154. Раздел CSS терялся в массиве order при экспорте/отображении;
contextObservations сохраняли данные. В v15 порядок исправлен.

У NavigatorDeviceMemory, Battery, Gamepad и базовых MediaDevices/Capabilities
в IDL Chromium нет RuntimeEnabled выключателя. Стандартный CDP также не имеет
RAM override. Поэтому новое ограничение явно программное: deviceMemory=8,
закрытые аппаратные entry points и измерение памяти/квоты. Не имитируется
нативность JavaScript функций и не заявляется невозможность определения guard.
CSS/DOM-проверки шрифтов, benchmarks, Math и canPlayType сохраняются.

FontFace constructor запрещает local-источники, включая CSS escapes и смешанный
url/local список. URL и binary fonts остаются доступны. Native CSS local-source
rendering restriction сохраняется. Это не запрет всех CSS/DOM способов
определения установленных шрифтов.

Для workers одного Runtime.evaluate на attachedToTarget недостаточно:
service-worker context может ещё не существовать. Debugger instrumentation
beforeScriptExecution приостанавливает первый скрипт; guard устанавливается
и читается обратно до выключения Debugger, которое возобновляет исполнение. Документы используют document-created
script. Startup fixture читает RAM и API в начале dedicated worker script,
а не после сообщения. Debugger после проверки выключается, чтобы debugger
statement сайта не оставлял worker на паузе.
Попытка ранней установки Debugger в service worker прервала main-script fetch
в Windows CI 37183889404. В итоговом strict mode ServiceWorker API закрыт
скриптом, Network.setBypassServiceWorker(true) применяется нативно; обнаруженные
service-worker targets закрываются через Target.closeTarget без resume.
Контроль сначала регистрирует настоящий worker и получает перехваченный ответ,
затем проверяет остановку target и исходный ответ host при bypass. Другие режимы
сохраняют service workers. Отдельные внешние контексты этим тестом не подтверждаются.

Эксперимент с Emulation.setDeviceMetricsOverride показал реальный предел:
main, same-origin и initial frames получили 1920×1080, а OOP iframe
сохранил физические 1024×768/1024×720 в CI. Прямая команда в дочернем target
запрещена Chromium. CI 37183068527 намеренно завершился ошибкой проверки
внешнего фрейма. Экран не наследуется OOP target, вопреки прежней гипотезе.
Команда не включена в выпуск 0.1.19: JS-подмена Screen оставила бы CSS device-size
утечку, а блокировка всех внешних/изолированных frames нарушила бы работу почты
и проверок сайтов. Для полного решения нужен другой браузерный backend либо
изменение Chromium с согласованной эмуляцией всех renderer widgets и CSS.
Выпуск сохраняет DPR 1 и реальные согласованные размеры; экран остаётся Visible.

В строгом режиме с прокси флаги --proxy-bypass-list=<-loopback>, --disable-quic
и --host-resolver-rules="MAP * ~NOTFOUND, EXCLUDE proxy-host" ограничивают
известные обходы. EXCLUDE оставляет разрешение самого узла прокси необходимым
для соединения. Native стенд использует локальные IPv4/IPv6 proxy endpoints,
HTTP destinations, неразрешимый target hostname и настоящий TLS CONNECT tunnel.
После закрытия прокси живые прямые receivers не должны получать новые запросы.
Это локальный контролируемый тест; системный DNS, все протоколы/процессы
и пользовательская сеть не объявляются полностью проверенными.

Источники дополнительно:
- [NavigatorDeviceMemory IDL](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/frame/navigator_device_memory.idl).
- [NavigatorBattery IDL](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/modules/battery/navigator_battery.idl).
- [NavigatorGamepad IDL](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/modules/gamepad/navigator_gamepad.idl).
- [Chromium proxy bypass](https://chromium.googlesource.com/chromium/src/+/main/net/docs/proxy.md#implicit-bypass-rules).
- [Debugger instrumentation breakpoint](https://chromedevtools.github.io/devtools-protocol/tot/Debugger/#method-setInstrumentationBreakpoint).

Итоговая native проверка: Windows CI 37185038261, commit
7e32676783e42c6270ed68585b6f5cb2e4f0fedf, Runtime 153.0.4234.48.
399 .NET / 62 JS; оба proxy endpoint families, TLS CONNECT и отказ прокси прошли.
Для EXCLUDE используется raw IPv6 hostname (::1), URI-скобки там не совпадают
с resolver hostname. Cached service worker control и subsequent stop/bypass
прошли в main/child. Пользовательский Runtime 154 требует нового отчёта 0.1.19.

## Следующий шаг: 0.1.22 / v17

Не добавлены подмены screen или метрик шрифтов: согласованность с CSS
и междоменными контекстами важнее формально одинаковых JS значений.
Добавлены измерения кодеков/таймера и два локальных iframe с проверкой
15 существующих ограничений. Короткая выборка таймера не доказывает
ни точную производительность CPU, ни отсутствие других таймеров.
Доступность WebCodecs не устанавливает аппаратное ускорение.
Шрифтовые alias и fallback могут давать одинаковые метрики; найденный
кандидат не является доказательством наличия отдельного файла шрифта.
Полное ограничение CSS/DOM-шрифтов и экрана требует работы с движком
либо одинаковой Windows-среды; эти остаточные источники не закрыты.

## 0.1.23: реальная утечка часового пояса OOP iframe

В пользовательском 0.1.22/v17 основной IANA timezone Europe/Riga
отличался от Africa/Nairobi в cross-origin iframe при одинаковом
текущем UTC+3. Root-only CDP override не переносится автоматически
в отдельный renderer. Выбранный timezone передаётся каждому связанному
target до startup resume. Для проверки добавлены force site isolation,
отрицательный root-only контроль и Date winter/summer snapshots.
Такой способ сохраняет нативные Date/Intl и сезонные переходы;
фиксированная подмена UTC offset не используется.
