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
Только первый сбор UI Automation в родительском WPF HWND не увидел
отдельный native popup; причина ожидания требует дополнительного наблюдения.
Наличие корректного файла само по себе не подтверждает завершение проверки.

## Изменение

DownloadStarting.Handled=false сохраняет нативные диалоги браузера, которые
не имеют полного аналога через DownloadOperation. Наша панель и выбор
конечного пути сохранены. «Подробнее» открывает диалог из живой вкладки
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
Windows CI и выпуск пока не завершены; этот отчёт будет дополнен результатами.

Ранее оставшиеся ручные пункты и ограничения:
[приёмка 0.1.57](acceptance-report-0.1.57.md).

## Источники

- [SDK Handled](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2downloadstartingeventargs.handled?view=webview2-dotnet-1.0.4258.31).
- [SDK OpenDefaultDownloadDialog](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.opendefaultdownloaddialog?view=webview2-dotnet-1.0.4258.31).
- [Microsoft feedback 4185](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4185): пример ожидания из-за скрытого unsafe-file prompt; это возможная причина, не доказательство для нашего EXE.
- [Microsoft feedback 5638](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5638): stale State после window.open; в данном probe событие и GUID не терялись.
