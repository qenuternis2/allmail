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

Изменения поведения приложения по результатам этого исследования не внесены.
Новые API CDP ниже исследованы по актуальной ветке Chromium/main и протоколу
tip-of-tree; их наличие и эффективность в пользовательском WebView2 154 ещё
нужно проверить нативно. Выводы о Windows Runtime 154 из JSON ограничены
фактически наблюдаемыми значениями и подтверждёнными проверками текущей версии.

## Источники

- [CDP Emulation](https://chromedevtools.github.io/devtools-protocol/tot/Emulation/): media, OS text scale, device metrics и headless-only screens.
- [CDP CSS.setLocalFontsEnabled](https://chromedevtools.github.io/devtools-protocol/tot/CSS/#method-setLocalFontsEnabled).
- [CDP Page.setFontFamilies](https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-setFontFamilies).
- [Blink MediaFeatureOverrides](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/css/media_feature_overrides.cc): поддерживаемые media features.
- [Blink LocalFontFaceSource](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/css/local_font_face_source.cc): проверка LocalFontsEnabled при создании локального источника.
- [Blink InspectorCSSAgent](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/inspector/inspector_css_agent.cc): состояние local_fonts_enabled и предупреждение о перезагрузке.
- [Chromium EmulationHandler](https://github.com/chromium/chromium/blob/main/content/browser/devtools/protocol/emulation_handler.cc): SetDeviceMetricsOverride отклоняется для target с parent/outer document.
- [ApproximatedDeviceMemory](https://github.com/chromium/chromium/blob/main/third_party/blink/common/device_memory/approximated_device_memory.cc): OS RAM, округление и границы.
- [Chromium Client Hints](https://github.com/chromium/chromium/blob/main/content/browser/client_hints/client_hints.cc): AddDeviceMemoryHeader использует ту же нативную оценку.
- [BatteryStatus defaults](https://github.com/chromium/chromium/blob/main/services/device/public/mojom/battery_status.mojom) и [Windows backend](https://github.com/chromium/chromium/blob/main/services/device/battery/battery_status_manager_win.cc).
- [WebView2 EnvironmentOptions](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environmentoptions): AdditionalBrowserArguments зависят от Runtime и не делают все Chromium switches гарантированными настройками WebView2.
