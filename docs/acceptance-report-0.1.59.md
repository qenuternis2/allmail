# Приёмка SecureBrowser 0.1.59

Дата: 8 октября 2026. Исправляются пустые штатные подробности загрузки
при ожидании завершения на 100%. 0.1.58 подтверждала только событие открытия
диалога и не проверяла его содержимое; повторный скриншот пользователя
выявил недостаточность такой проверки.

## Причина и изменение

DownloadStarting.Handled=true скрывает файл из штатного списка. На одном
публичном EXE и одинаковой защите при true получено пустое окно; при false
получен файл с предупреждением «isn't commonly downloaded».
Теперь Handled=false, а выбранный приложением путь, панель и native operation
сохранены. Браузер продолжает проверять файл. «Готово» не выставляется только
по размеру, существованию файла или совпадению SHA-256.

Подробнее передаёт идентификатор загрузки и открывает её исходный контроллер.
Другая вкладка может показывать только собственные недавние загрузки.
Контроллер закрытой вкладки сохраняется в отдельном окне фоновой загрузки;
для подробностей это окно показывается, без новой навигации и без восстановления
вкладки. Закрытие подробностей скрывает окно и сохраняет загрузку. Завершение
загрузки и закрытие профиля освобождают окно/контроллер прежним путём.
Неизвестный идентификатор и закрытое поколение не открывают чужие подробности.
Публичные DownloadInfo/IBrowserViewHost и схема профилей не менялись.

## Подтверждённые проверки

До изменений: Linux solution build без предупреждений/ошибок; 667 Core
и 108 JavaScript без ошибок и пропусков. После изменений Windows smoke
собран на Linux. WPF/WebView2 исполнялись только в изолированной Windows 11 ARM64,
через x64 emulation; Windows Server не использовался.

[Сравнение скрытого и видимого списка](https://github.com/qenuternis2/allmail/actions/runs/37752743485)
подтвердило различие содержимого на Runtime 154.0.4258.62, SDK 1.0.4258.31,
Tracking Strict и graphics policy 10, без прокси. Снимок получен через PrintWindow
конкретного Chrome_RenderWidgetHostHWND; обычный desktop capture на этом runner
показывает OOBE и не считается подтверждением интерфейса.
[Проверка версии 0.1.59 и нативного меню](https://github.com/qenuternis2/allmail/actions/runs/37761097247)
подтвердила наличие файла и команды Keep в отдельном нативном окне меню,
совпадение SHA-256 и сохранение InProgress без автоматического подтверждения.
Keep/Run не выбирались. [Receipt](security-evidence/download-public-exe-0.1.59.json);
[снимок меню](security-evidence/download-warning-menu-0.1.59-0.png).


[Полный Windows CI итогового main/tag](https://github.com/qenuternis2/allmail/actions/runs/37763177174)
прошёл на 9e6b1bb: обе поставки, 667 Core, 108 JS, native загрузки, tabs,
privacy/proxy/lifecycle, DPI и все 5 операций установщика.
Проверены наличие known.bin в штатном списке после фактического WPF-клика
и закрытия исходной вкладки, сохранение паузы, закрытие окна подробностей
без отмены/восстановления вкладки, неизвестный ID, устаревшее поколение,
HTTP Range с точной проверкой payload, blob, параллельные/повторные загрузки,
window.close и освобождение фоновых контроллеров.
[Receipt Windows](security-evidence/windows11-0.1.59.json).
DPI 100/125% — DesktopScale; 150/200% — SyntheticWM_DPICHANGED.

## Опубликованный выпуск

[Релиз 0.1.59](https://github.com/qenuternis2/allmail/releases/tag/v0.1.59)
опубликован после [проверок tag](https://github.com/qenuternis2/allmail/actions/runs/37763470629)
на том же commit 9e6b1bb9e3d124139c3ba69590fcf5997210fc26.
ZIP и установщик скачаны из опубликованного релиза: SHA-256 совпадает
с sidecar и digest GitHub, все CRC ZIP проходят. Версия приложения 0.1.59
и informational version соответствуют tag; .NET и WPF runtime — 10.0.12.
coreclr.dll и PresentationFramework.dll совпадают с официальными NuGet runtime packs.
[Receipt выпуска](security-evidence/runtime-0.1.59.json).
Эти подтверждения добавлены в main после публикации; tag не перемещался.

## Границы проверки

На публичном EXE браузер продолжал ожидать решения о редко скачиваемом файле.
Предупреждение и доступность Keep подтверждены; Keep/Run не выбирались, переход именно этого
EXE в Completed после ручного Keep не проверен. Наличие SHA-256 подтверждает
идентичность файлу релиза, а не его безопасность. Интерактивные действия
SmartScreen/антивируса на ПК пользователя остаются ручной проверкой.

После обновления необходимо перезапустить приложение и скачать файл заново:
Handled применяется при начале загрузки. Старые скрытые записи не изменяются;
профили, cookies и пользовательские файлы не удаляются.
Прочие ручные пункты и ограничения сохранены из [0.1.57](acceptance-report-0.1.57.md).
Authenticode без предоставленного сертификата не настроена.

## Источники

- [Microsoft: Handled](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2downloadstartingeventargs.handled?view=webview2-dotnet-1.0.4258.31).
- [Microsoft: OpenDefaultDownloadDialog, recent/past downloads](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.opendefaultdownloaddialog?view=webview2-dotnet-1.0.4258.31).
- [Microsoft Feedback 4185: hidden unsafe-download prompt](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4185).
