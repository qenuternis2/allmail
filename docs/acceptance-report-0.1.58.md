# Приёмка SecureBrowser 0.1.58

Дата: 8 октября 2026. Исправляется недоступность штатных решений загрузки
в нашей панели. Исходная ошибка пользователя: FindCopy EXE получил все
байты, но «Сохранение файла…» не завершается; 0.1.57 не помогла.

## Воспроизведение

[Windows 11 probe](https://github.com/qenuternis2/allmail/actions/runs/37743874804)
на runtime 154.0.4258.62, Tracking Strict, graphics policy 10, без прокси:
во всех трёх случаях (та же вкладка/новая вкладка, hidden/native UI)
получены 73825766 байтов; конечный файл имеет SHA-256
`6aacb50429b3f5eef2da8a9a735e9f5384c0934a2d40504358af8713cbe7afba`
из публичного GitHub release. SDK State и CDP state остаются InProgress.
GUID совпадает; SDK/CDP относятся к одному контроллеру и тому же конечному
URL release-assets.githubusercontent.com. Это не ошибка сопоставления GUID,
редиректа или потери parent-controller event в этих трёх случаях.
[Данные](security-evidence/download-public-exe-0.1.58-baseline.json).
Дополнительный [probe](https://github.com/qenuternis2/allmail/actions/runs/37745722529)
зафиксировал в History временного профиля `danger_type=5`
(`DOWNLOAD_DANGER_TYPE_UNCOMMON_CONTENT`) с тем же GUID, что и живая загрузка.
Браузер пометил этот EXE как редко скачиваемый; автоматического разрешения нет.
[Классификация и ограничения](security-evidence/download-public-exe-0.1.58-classification.json).
History держится Runtime в exclusive lock: чтение выполнено только после
явного закрытия тестового профиля. Его state=2/interrupt_reason=40/received_bytes=0
не трактуются как состояние до закрытия: для него используются живые SDK/CDP
значения и проверенный конечный файл. Классификация взята из официального
Chromium enum, значения которого сохраняются и не перенумеровываются.

Само создание native popup наблюдалось через SDK. UI Automation не увидел
текст предупреждения на runner; desktop screenshot перекрыт Windows OOBE и
не является доказательством видимого предупреждения. Keep/Open/Run не нажимались,
поэтому завершение после принятия предупреждения не заявляется как проверенное.
Наличие корректного файла само по себе не подтверждает завершение проверки.

## Изменение

DownloadStarting.Handled=true и установленный путь сохранены: постоянный
прогон с постоянным Handled=false однажды не дождался SDK Interrupted при
native HTTP Range retries (другой прогон прошёл; причина различия не доказана).
Итоговое изменение сохраняет прежний режим начала загрузки и retry,
добавляя только явное открытие штатных подробностей при ожидании.
Нативный диалог явно открывается через OpenDefaultDownloadDialog спустя
30 секунд ожидания при 100%. Этот вызов выполняется вне SDK callback,
только для видимого профиля текущего поколения и один раз после успешного
показа. Скрытый профиль получает его после выбора. Ошибка открытия диалога
не отменяет загрузку. Штатные решения не имеют полного аналога через
DownloadOperation. Наша панель и выбор конечного пути сохранены. «Подробнее» открывает диалог из живой вкладки
текущего профиля; закрытая исходная вкладка не открывается вновь.
Для закрытого/устаревшего поколения действие не выполняется.
30-секундная подсказка указывает на подробности вместо бесконечного
предложения ждать. Автоматическая отмена/Keep/Run отсутствуют;
SmartScreen, политика файлов и антивирус не отключаются.

## Проверки

До изменений: Linux solution build без предупреждений/ошибок, 667 Core
без пропусков. После изменений сборка App и Windows smoke на Linux
прошла; исполнение WPF выполняется только в Windows VM.
Новые проверки: WPF кнопка подробностей после закрытия исходной вкладки,
сохранение паузы, отказ для закрытого поколения, видимость для pending/
Interrupted (включая non-resumable) и отсутствие для Completed/Cancelled.
[Проверка итогового исправления на публичном EXE](https://github.com/qenuternis2/allmail/actions/runs/37747351304)
прошла на `c7e9a007bfa52166405aaa72832f36d8b30e0090`, Runtime 154.0.4258.62.
До ручного OpenDefaultDownloadDialog через 30 секунд после всех байтов
получено событие открытия штатного диалога. Payload SHA-256 совпал;
InProgress и UNCOMMON_CONTENT сохранены, без Keep/Run/автоматического Completed.
[Evidence](security-evidence/download-public-exe-0.1.58-fix.json).

Первый полный успешный [прогон](https://github.com/qenuternis2/allmail/actions/runs/37746094756)
на `3beb44defc425eed572c28060711e83ac22b577e` проверил кнопку подробностей,
DPI и установщик. Он предшествует итоговому возврату Handled=true и
автоматическому открытию по 30-секундному ожиданию; не считается проверкой
этого изменённого кода.
Итоговый [Windows CI](https://github.com/qenuternis2/allmail/actions/runs/37747357686)
прошёл на `c7e9a007bfa52166405aaa72832f36d8b30e0090`: обе поставки,
667 Core без пропусков, 108 JS, все native сценарии загрузок/вкладок,
новая WPF кнопка, DPI и все 5 операций установщика.
[Evidence](security-evidence/windows11-0.1.58.json).
DPI 100/125% — настоящий DesktopScale; 150/200% — SyntheticWM_DPICHANGED.
[CI main](https://github.com/qenuternis2/allmail/actions/runs/37748840218) и
[release CI](https://github.com/qenuternis2/allmail/actions/runs/37748845442)
полностью прошли на `e4e64db3b8299a8b6250ba8234d6b36a1e828f4a` / tag `v0.1.58`.
[ZIP и EXE опубликованы](https://github.com/qenuternis2/allmail/releases/tag/v0.1.58).
Скачанные ZIP и installer совпали с sidecar SHA-256 и GitHub asset digest;
CRC всех членов ZIP проверены. .NET/WPF 10.0.12, CLR/WPF DLL побайтово
совпали с официальными NuGet packs. Версия 0.1.58 и informational version
с точным commit tag подтверждены в архиве.
[Evidence поставки](security-evidence/runtime-0.1.58.json).
ACCEPTANCE.md внутри immutable tag/ZIP фиксирует source проверку до публикации;
итоговые receipts опубликованы здесь после проверки скачанных файлов.

Ранее оставшиеся ручные пункты и ограничения:
[приёмка 0.1.57](acceptance-report-0.1.57.md).

## Источники

- [SDK Handled](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2downloadstartingeventargs.handled?view=webview2-dotnet-1.0.4258.31).
- [SDK OpenDefaultDownloadDialog](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.opendefaultdownloaddialog?view=webview2-dotnet-1.0.4258.31).
- [Microsoft feedback 4185](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4185): пример ожидания из-за скрытого unsafe-file prompt; для нашего EXE отдельно зафиксирована классификация UNCOMMON_CONTENT.
- [Microsoft feedback 5638](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5638): stale State после window.open; в данном probe событие и GUID не терялись.

- [Chromium DownloadDangerType](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/download/public/common/download_danger_type.h): persisted enum, UNCOMMON_CONTENT=5.
