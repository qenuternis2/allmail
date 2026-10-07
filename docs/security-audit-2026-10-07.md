# Аудит безопасности SecureBrowser — 7 октября 2026

Проверена исходная ревизия `a3beb775a4a22bc64d023fc07f8212f1bdda9e9a`
(`main`, v0.1.54) и состав опубликованного ZIP. Исправления исходников:
`a6db188`. Рабочая копия перед изменениями была чистой; `AGENTS.md` не найден.
Публичные сигнатуры, схема настроек, маршруты прокси, установка, обновление и
зависимости не изменены. Новый релиз этим аудитом не выпущен; установленная
v0.1.54 пока содержит прежний код и runtime.

## Метод и границы

Сначала прочитаны исходники, manifests, четыре workflows, build/install/sign/update
скрипты и инструкции. Приложение, установщики и скачанные EXE при статическом
анализе не запускались. ZIP исследован как данные, без исполнения его содержимого.
После этого выполнены проверки в изолированной облачной Linux-среде с отдельным
каталогом тестов и синтетическими файлами/секретами. Доступ к личным документам,
браузеру или буферу обмена пользователя для аудита не использовался.

Рассмотрены вредоносный сайт/импорт/имя загрузки, сетевой наблюдатель, другой
локальный процесс и предварительно изменённые файлы профиля. Права атакующего
не следует смешивать: доступ процесса той же Windows-учётной записи обычно уже
позволяет читать её Credential Manager и файлы. Подмена ссылок ниже не доказывает
удалённое выполнение кода или повышение привилегий.

## Подтверждённые находки

### S1 — P1 / высокий: уязвимые runtime в поставке; требуется согласование

**Участок:** `global.json:2` (SDK 10.0.100), `build.ps1:21–22`
(self-contained publish), `SecureBrowser.runtimeconfig.json` и
`SecureBrowser.deps.json` внутри опубликованного ZIP v0.1.54.
Фактически включены `Microsoft.NETCore.App` и `Microsoft.WindowsDesktop.App`
**10.0.0**, включая соответствующие runtime packs win-x64. Это не вывод только
по номеру SDK: проверены метаданные самого архива, его SHA256 совпадает с
asset digest опубликованного release в GitHub API; сохранены SHA256 и
[свидетельство](security-evidence/runtime-0.1.54.json).

**Условия/ущерб:** версия входит в официальные affected ranges Microsoft:

| Бюллетень Microsoft | Компонент / условия из бюллетеня | Последствие / исправление |
| --- | --- | --- |
| [CVE-2026-62897](https://github.com/dotnet/announcements/issues/434) | WPF, Windows, локальный вектор, действие пользователя; CVSS 7.0 | Integer overflow, выполнение кода; 10.0.0–10.0.10 → 10.0.11 |
| [CVE-2026-70354](https://github.com/dotnet/announcements/issues/432) | WPF, Windows, локальный вектор, действие пользователя; CVSS 7.8 | Out-of-bounds write, выполнение кода; 10.0.0–10.0.10 → 10.0.11 |
| [CVE-2026-50528](https://github.com/dotnet/announcements/issues/417) | .NET System.Net.Security/SslStream; сетевой вектор; CVSS 8.2 | Обход проверок авторизации при TLS; 10.0.0–10.0.9 → 10.0.10 |

Наличие уязвимых компонентов подтверждено. Конкретная эксплуатация через это
приложение **не воспроизведена**. WPF не получает произвольный XAML с сайтов;
браузерный TLS реализован WebView2, а .NET HttpClient используется, например,
для GeoIP. Нельзя автоматически приписать приложению каждый сценарий runtime-CVE
или утверждать, что оно принимает любой неверный сертификат.

**Минимальное исправление:** согласовать SDK **10.0.112** (та же feature band),
явно закрепить runtime **10.0.12**, проверить два self-contained publish и
выпустить новую версию. Это текущий patch из официальных
[release metadata .NET 10](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
на дату аудита. [Готовый патч](security-proposals/runtime-update.patch) пока
**не применён**. Обновление системного .NET не заменяет runtime внутри старого
self-contained ZIP; пользователям потребуется новая поставка.

### S2 — P2 / средний: удаление через связанный родитель; исправлено

**Участок:** `src/ProtonProfiles.Core/Storage/SafeProfileDeleter.cs:38`,
`ValidateOwnership`, вызовы reset/delete и проверки UDF при открытии.
Раньше проверялись `Profiles` и компоненты ниже него, но не корень приложения
и его родители. Обычный `Profiles` под symlink/junction проходил проверку.

**Условия/ущерб:** заранее созданная ссылка в родительском пути плюс reset/delete
могли удалить содержимое внешнего дерева. В fixture это собственный тестовый
каталог за пределами разрешённого managed root; реальных документов нет.
Нужна локальная подмена/перенаправление, а не только посещение сайта. Средний
риск из-за возможной необратимой потери данных при ограниченных предпосылках.

**Минимальное исправление:** проверять также всех родителей и dangling links.
Внесено. Четыре случая reset/delete × ссылка на корень/родителя сначала удаляли
sentinel, после исправления отклоняют операцию и сохраняют его. Это соблюдение
существующего запрета ссылок. Гонки с конкурентной заменой пути и hard links
этой проверкой полностью не устранены; для сильного противника нужны операции
по дескрипторам и отдельная проверка на NTFS.

### S3 — P2 / средний: запись миграции по ссылке; исправлено

**Участок:** `src/ProtonProfiles.Core/Storage/WebViewDefaultProfileMigration.cs:21–37`,
служебные `.pending`, `.complete`, `.complete.tmp` и назначения backup.
`File.WriteAllText` не проверял служебные записи на reparse points.

**Условия/ущерб:** локальная подмена commit-файла ссылкой, после которой обычная
миграция старого профиля перезаписывала внешний файл текстом marker. Это
воспроизведено на тестовом sentinel. Предпосылки локальные; удалённого пути
создания такой ссылки не найдено. Средний риск повреждения данных.

**Минимальное исправление:** отклонять ссылочные служебные записи до recovery/
записи и проверять назначения backup даже при dangling links. Внесено.
Положительный контроль dangling backup уже безопасно завершался до изменения;
он не объявляется новой доказанной уязвимостью. Защита от конкурентной замены
родителя/hard links остаётся ограничением этого файлового threat model.

### S4 — P2 / средний с действием пользователя: формулы в диагностике; исправлено

**Участок:** `src/ProtonProfiles.Core/Diagnostics/ConnectionLog.cs:127`, `ToTsv`;
`src/ProtonProfiles.App/Dialogs/ProfileDiagnosticsWindow.cs:153`, `CreateGrid`.
CDP передаёт HTTP method, задаваемый сайтом. Например, `+1+2` — валидный HTTP token,
который ранее попадал в TSV и копируемую строку без защиты spreadsheet.

**Условия/ущерб:** сайт создаёт запрос с таким методом, пользователь экспортирует/
копирует диагностику и открывает её в spreadsheet, распознающем формулы.
Возможна нежелательная формула, а в некоторых конфигурациях — внешние обращения.
Эксплуатация Excel/DDE/выполнение команд **не проверялись и не заявляются**.

**Минимальное исправление:** при TSV/копировании сделать текстовые поля,
начинающиеся после whitespace с `= + - @`, буквальными (апостроф), убрать
разделители строк/ячеек. Внесено в оба пути. Исходный журнал и JSON не изменены;
обычные строки/числа сохраняются. Шесть отрицательных случаев и GET-контроль
прошли. Нативный тест production copy event не обращается к OS clipboard;
он также прошёл в Windows 11 CI (ссылка в таблице проверок).

### S5 — P1 при недоверенной сети: Basic-пароль HTTP-прокси виден на пути; согласование

**Участок:** `src/ProtonProfiles.Core/Network/AuthenticatedProxyRelay.cs:25–26,74,82–84`;
`src/ProtonProfiles.Core/Model/ProxyEndpoint.cs:31–39`.
Поддержан только HTTP upstream. Basic формируется как Base64, передаётся в
`Proxy-Authorization` по обычному `TcpClient.GetStream()`, без SslStream.
Это подтверждают и существующие fixture-тесты приёма заголовка upstream.

**Условия/ущерб:** при прокси за пределами защищённого localhost/VPN любой
наблюдатель соответствующего незашифрованного сетевого участка может извлечь
логин/пароль, расходовать аккаунт прокси или получить доступ к разрешённой через
него сети. HTTPS сайта внутри CONNECT не шифрует первоначальный proxy-auth
обмен. Высокий условный риск раскрытия секрета; на защищённом транспорте ниже.

**Минимальное исправление:** добавить HTTPS upstream с обязательной обычной
проверкой сертификата и явным выбором транспорта. До этого — использовать
доверенный VPN/локальный защищённый туннель и явно предупреждать о HTTP Basic.
Не изменено: это расширение сетевой функциональности/формата proxy endpoint,
которое нужно согласовать и проверить с HTTP/HTTPS/CONNECT fixtures.

### S6 — P2 / средний на многопользовательском ПК: анонимный loopback relay; согласование

**Участок:** `AuthenticatedProxyRelay.cs:13,35–49,74`.
Listener привязан только к `127.0.0.1` на случайном порту, но не проверяет
идентичность downstream перед добавлением upstream credentials.

**Условия/ущерб:** другой локальный процесс, в том числе другой OS-пользователь
с доступом к loopback, может найти порт и направить valid absolute-form HTTP/
CONNECT через оплаченный/привилегированный прокси владельца. Это видно из кода
и существующих тестов анонимного клиента. Сырые секреты клиенту не возвращаются;
доступ с LAN и эксплуатация непосредственно веб-страницей не подтверждены.
Неограниченный счётчик подключений также оставляет локальный DoS.

**Минимальное исправление:** случайный capability на экземпляр relay,
проверяемый до соединения upstream, безопасная передача его WebView2 через
proxy-auth callback; ограничение числа одновременных клиентов. Реальные upstream
credentials по-прежнему хранить только внутри relay. Изменение необходимо
согласовать: оно затрагивает работу прокси и требует native positive controls,
проверки отказа без capability и проверки взаимодействия нескольких OS-users.

### S7 — P2 / средний: overwrite после изменения выбранного имени; согласование

**Участок:** `src/ProtonProfiles.App/MainWindow.xaml.cs:755–767`, `ChooseDownloadPath`.
SaveFileDialog подтверждает overwrite для `target`, затем имя повторно
санитизируется и WebView2 получает другой `final`.

**Условия/ущерб:** пользователь редактирует имя, например `e\u0301.txt`, а
`é.txt` уже существует. Нормализация NFC создаёт collision для другого файла,
не охваченного первоначальным overwrite prompt. Кодовый путь и нормализация
подтверждены; фактическое поведение Win32 picker/WebView2 overwrite не проверено.
Возможна случайная потеря выбранного существующего файла; user interaction нужен.

**Минимальное исправление:** если конечный путь изменился и существует, показать
подтверждение именно этого файла с отменой по умолчанию. Подготовлен
[патч](security-proposals/download-overwrite-confirmation.patch), не применён
до согласования изменения диалога. Проверить настоящий picker в отдельной VM,
включая NFC, замену запрещённых символов, Cancel и обычную подтверждённую замену.

## Дополнительная защита и рекомендации

Здесь не заявляется подтверждённая текущая эксплуатация.

| ID / риск | Файл и предпосылки / возможный ущерб | Минимальная мера / состояние |
| --- | --- | --- |
| H1 / низкий, потенциальный секрет в логе | `src/ProtonProfiles.Core/Credentials/ICredentialStore.cs:3`: record генерировал `ToString` с username/password; действующего production logger, печатающего record, не найдено | **Внесено:** безопасный `ToString`, поля/equality/auth сохранены, целевой тест |
| H2 / низкий, поиск исполняемого файла | `ProfileDiagnosticsWindow.cs:319`, `OpenLogFolder`: относительный `explorer.exe` зависел от Windows search; вредоносный локальный EXE требует права записи в найденную локацию | **Внесено:** абсолютный системный Explorer и `ArgumentList`; команда открытия папки сохранена. Не обещает защиту от уже скомпрометированной ОС |
| H3 / средний, build host | `scripts/Build-Installer.ps1:14–25`: скачанный Inno installer проверяется SHA256/Authenticode, но существующий cached `ISCC.exe` исполняется без повторной проверки; требуется локальная подмена cache | Повторная проверка compiler/toolchain по доверенному manifest/hash до каждого исполнения; предложение требует согласования изменения build/install пути. Cache poisoning не воспроизведён |
| H4 / средний, подмена поставки | `scripts/Sign-Release.ps1:5`, `.github/workflows/release.yml`: без certificate release unsigned. SHA256 рядом с EXE не удостоверяет издателя при подмене обоих файлов | Настроить Authenticode доверенным certificate; код signing уже предусмотрен. Нужен сертификат владельца, установка/публикация меняется только после согласования |
| H5 / средний, supply chain | Четыре `.github/workflows/*.yml`: mutable `actions/*@v4`; release job получает `contents: write` также для build. Нет NuGet lockfile/locked restore | Закрепить actions по reviewed SHA, использовать lockfile и разделить минимальные read/build и write/publish permissions. Компрометация не обнаружена; изменение pipeline согласовать |
| H6 / низкий–средний, локальная приватность | `src/ProtonProfiles.Core/Storage/BrowserTabsStore.cs:48`, SQLite settings, `src/ProtonProfiles.Core/Diagnostics/ConnectionLogFile.cs:96`: URL вкладок и пути сайтов сохраняются локально, URL может включать чувствительный query/fragment; стороннего чтения не обнаружено | Согласовать DPAPI для tab snapshots, минимизацию URL и сроков хранения. Нельзя просто удалить параметры: это нарушает восстановление вкладок. Windows ACL/CurrentUser не защищают от вредоносного процесса той же учётки |
| H7 / низкий–средний, доверие GeoIP | `src/ProtonProfiles.Core/Privacy/MailfudGeoIpUpdater.cs:25,45,145`, `src/ProtonProfiles.Core/Privacy/GeoIpTimeZoneDatabase.cs:59`: HTTPS, размер/структура/date/rollback проверки, но нет независимо проверяемой подписи данных. При компрометации издателя можно подменить timezone | Использовать detached signature/доверенный digest manifest, если издатель предоставит; hash, скачанный от того же скомпрометированного источника, не решает вопрос. Исполнение скачанной базы как кода не найдено |
| H8 / низкий, раскрытие IP при диагностике | `src/ProtonProfiles.App/Diagnostics/fingerprint.html:32,70–73`: запросы ipinfo/ipify/api6/httpbin при открытии probe. Видимое предупреждение о внешних сервисах уже есть, cookies omitted; это не скрытая фоновая телеметрия | **Уточнено:** добавлен фактически используемый `api6.ipify.org` в список. Отдельное предварительное opt-in можно согласовать как изменение UX |
| H9 / средний–высокий при требовании строгой сетевой изоляции | `src/ProtonProfiles.Core/Network/ProxyArguments.cs:11–25`, `src/ProtonProfiles.App/Browser/WebView2Engine.cs:138`: маршрутизация WebView2 через experimental browser flags, без OS network sandbox. Не доказано, что все DNS/UDP/IPv6/background запросы любого Runtime всегда идут через прокси; возможный ущерб — раскрытие IP/маршрута | Отдельный VM packet capture при запуске, реконнекте, reset и обновлении Runtime; OS/VM isolation при необходимости строгой гарантии. Это ограничение модели, не воспроизведённый новый bypass. Изменение маршрутов/системных прав согласовать |

## Проверенные защитные свойства

- **Команды/процессы:** пользовательский URL не конкатенируется с shell command.
  External browser получает только валидированный HTTP/HTTPS после явного действия.
  PowerShell uninstall использует абсолютный системный путь, фиксированный
  EncodedCommand, NoProfile/NonInteractive; URL/импорт не попадают в script.
- **Файлы:** UUID managed paths; ограниченный импорт до 1 MiB/1000 записей,
  строгие JSON-поля, новый UUID, без импорта credentials/permission grants/UDF.
  Имена загрузок отбрасывают server path, ADS/Windows-invalid и reserved names.
  GeoIP gzip — один bounded stream до 256 MiB, без путей ZIP entries; обычного
  zip-slip пути не найдено. Ссылки/гонки локальной FS имеют отмеченные ограничения.
- **Права/удаление:** manifest `asInvoker`, Inno `PrivilegesRequired=lowest`,
  per-user install. Services/autostart/system modifications приложением не найдены.
  Удаление пользовательских данных требует выбора/специального подтверждённого
  режима; ownership lock и проверка живого Runtime блокируют опасный reuse/delete.
  Вложения вне managed tree и общий WebView2 Runtime не удаляются. Пользователь
  может сам запустить asInvoker с повышением; forced unelevated launch не реализован.
- **Разрешения:** неизвестные permissions deny, camera/mic/geo/clipboard policy
  ограничены профилем/origin. WebView native permissions не сохраняются без policy;
  hardware privacy exceptions не заменяют запрос согласия. Прямого обхода доступа
  к документам/микрофону/камере из страницы через host API не найдено.
- **Секреты:** Windows Credential Manager CurrentUser, в SQLite только references.
  Browser arguments и штатные diagnostic exports не содержат proxy password;
  URL query/userinfo редактируются для логов. Managed strings не гарантируют
  очистку всех копий секретов из памяти; crash dumps могут быть чувствительными.
- **TLS/сеть:** отключения certificate validation не найдено. GeoIP HttpClient не
  отправляет default credentials/cookies и не follows redirects. Опция обновления
  глобальная opt-in, UI прямо предупреждает о системной сети/IP Mailfud. Авто
  timezone раскрывает только IP сервисам ipify через профиль, с UI disclosure.
  Единственный production local listener — описанный loopback proxy relay.
- **WebView/IPC:** отдельный UDF профиля, сайты без host objects/web messaging,
  autosave/general autofill/OS SSO/extensions отключены; sandbox не отключается.
  IPC включён только для bundled диагностической страницы. `FingerprintProbePage`
  проверяет scheme/host/default port/path/source; внешняя навигация, popups и
  downloads этой страницы блокируются. Arbitrary privileged command handler не
  найден. CDP используется приложением, удалённый debugging port не включён.
- **Установка/обновление:** executable auto-updater приложения отсутствует;
  новый ZIP/setup устанавливается вручную. Evergreen обслуживает Microsoft.
  CI runtime bootstrapper проверяет Microsoft Authenticode, Inno download — pinned
  SHA256 и valid signature. App-lifetime updater загружает только GeoIP data,
  две базы валидируются до atomic commit, предусмотрены bounded retries/rollback.
  NuGet build targets WebView2/SQLite просмотрены на нестандартный execution: подозрительных
  install/download commands не найдено; WebView2 native-loader copy ожидаем.

## Зависимости и официальные источники

На 2026-10-07 сопоставлена **31** уникальная resolved package/version пара из
assets Core/App/Core.Tests/Smoke с официальным
[NuGet vulnerability feed](https://api.nuget.org/v3/vulnerabilities/index.json),
снимок `2026-10-06T23:47:38.9099889Z`; список и ranges сохранены в
[JSON](security-evidence/nuget-2026-10-07.json).
Совпадений affected ranges нет. У SQLitePCLRaw.lib.e_sqlite3 **2.1.12** есть
advisory для **≤2.1.11**, у test-only Newtonsoft.Json **13.0.3** — для **<13.0.1**;
применять их к текущим версиям неправильно. В release native SQLite — **3.53.3**.
WebView2 SDK **1.0.4258.31** не равен Evergreen Runtime: runtime обновляется отдельно;
v0.1.54 и текущий Windows CI — **154.0.4258.62**, не измерен на ПК пользователя.

NuGet scan не покрывает весь implicit runtime pack, native browser/OS и все
неопубликованные проблемы. Поэтому отсутствие совпадений не отменяет S1.
Microsoft SDK advisories [CVE-2026-58649](https://github.com/dotnet/announcements/issues/441)
(`dotnet watch` BrowserRefreshServer) и
[CVE-2026-69806](https://github.com/dotnet/announcements/issues/442)
(`dotnet watch` Aspire, Linux) относятся к части старого SDK, но этот инструмент
не используется workflows и не входит в Windows поставку; Linux-only вектор
не объявляется Windows endpoint vulnerability. ASP.NET/COSE из отдельных
бюллетеней не объявлены зависимостями приложения без фактического наличия.
Дополнительно проверены официальные [SQLite release history](https://www.sqlite.org/changes.html)
и [advisories издателя Inno Setup](https://api.github.com/repos/jrsoftware/issrc/security-advisories).
SQLite уже предлагает **3.53.4** с исправлениями проблем 3.53.0–3.53.3; описание
не позволяет объявить каждый bug CVE или достижимым через приложение. Рекомендуется
согласованное обновление native bundle при доступности совместимого пакета.
Списки опубликованных advisories Inno и четырёх используемых `actions/*`
(официальные repository security-advisories endpoints) пусты на дату аудита;
это ограничение публикуемых сведений, не доказательство безопасности toolchain.
Фактические action SHAs этого CI сохранены в Windows evidence; tags не закреплены
в workflows, рекомендация H5 остаётся.

В официальных [Edge security release notes](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-relnotes-security)
**5 октября** указан 154.0.4258.62, а **6 октября** Microsoft сообщает, что готовит
следующее исправление Chromium. Это рекомендация следить за Evergreen и обновить
после выпуска. По этой записи нет точного списка CVE/ranges для WebView2, поэтому
она не выдается за подтверждённую эксплуатацию используемого runtime. Нижний
порог major 154 в CI не доказывает наличие всех patch-исправлений; проверять нужно
полный номер фактически установленного runtime. Официальные источники доступны;
полного независимого browser/OS pentest не было. Default GITHUB_TOKEN permissions
репозитория прочитать не удалось: permissions API вернул 403 (integration scope);
оценка полномочий основана на явном `contents: write` release workflow.

## Проверки и оставшаяся верификация

| Проверка | Результат |
| --- | --- |
| Исходные Core tests, до изменений | 653 pass, 0 skipped |
| Исходные четыре JS-набора | 108 pass |
| Новые security fixtures на старом коде | 12 fail, 2 pass (GET и уже защищённый dangling backup) |
| Те же fixtures после исправлений | 14 pass |
| Весь Core после исправлений | 667 pass, 0 skipped |
| Четыре JS-набора после изменения disclosure | 108 pass |
| Release solution, Experimental Proxy, native smoke compile | Успешно, 0 warnings/errors |
| C# analyzers verify-no-changes; `git diff --check` | Успешно |
| Native Win11 ARM / DPI / installer execution именно этих изменений | [Успешно, 06ddce2](https://github.com/qenuternis2/allmail/actions/runs/37656370652): 667 Core, 108 JS, production clipboard event, WebView2 и 5 installer operations |

Логи/TRX — в игнорируемых `artifacts/security-audit/`. Исходные DLL/runtime EXE
и installers не запускались на реальном пользовательском ПК. На Linux нативный
WebView2 не запускается; компиляция не заменяет исполнение WPF.

[Windows evidence](security-evidence/windows11-2026-10-07.json) сохраняет SHA,
полный Runtime version, action SHAs и границы DPI. Runner — Windows 11 Enterprise
ARM64, x64 приложение под эмуляцией. `Get-ComputerInfo.WindowsProductName`
содержит старое registry имя «Windows 10 Enterprise», но `OsName` — Microsoft
Windows 11 Enterprise; initialization script отдельно проверяет OS/build/architecture.
100%/125% — реальный DesktopScale; **150%/200% — SyntheticWM_DPICHANGED**,
фактическая смена desktop scale для них не выполнена. Native smoke успешно
проверил clipboard handler, существующие permission/proxy/privacy fixtures и DPI;
installer — production GUI, metadata hash, preserve/remove modes, внешнее вложение
и общий Runtime. Это синтетические профили в disposable Windows VM.

Остаются отдельные проверки в изолированной **Windows 11**, не Server: реальный
150%/200% desktop DPI и multi-monitor, install/uninstall под стандартной
неадминистративной учётной записью (права токена CI не измерены),
настоящий SaveFileDialog (S7), новый
ancestor fixture именно с NTFS junction вместо symlink, cross-user loopback
relay, полный packet capture DNS/UDP/IPv6/background и negative TLS. Permission
fixtures не заменяют тесты с настоящим микрофоном/камерой и OS privacy settings.
Для TLS exploit/fuzz malformed fonts/images нужна disposable VM без реальных
секретов. Также остаются uninstall с занятой/недоступной FS и сохранением всех
реальных сессий после обновления Evergreen; silent uninstall error dialog может
блокировать автоматизацию, но предыдущий timeout не доказывает потерю данных.

**Следующий согласуемый шаг:** применить готовый runtime patch и выпустить новую
версию после проверки её фактических packs; отдельно подтвердить дополнительный
overwrite prompt. HTTPS proxy/capability, signing, pipeline changes и шифрование
локальных URL — отдельные изменения назначения/совместимости, не включены скрыто
в этот аудит. Приложение не объявляется «полностью безопасным» по этим проверкам.
