# Proton Profiles для Windows

Менеджер профилей для ручной работы с существующими аккаунтами Proton Mail через официальный веб-интерфейс. Каждый профиль — отдельный WebView2-окружение со своей папкой данных, своими настройками и напоминаниями о посещении. Реализация по спецификации v1.1 (`proton_profiles_windows_spec_en.md`).

**Статус: прототип (этапы 1–2). Собирается, ядро покрыто тестами, на Windows не запускался.** Подробно — [docs/acceptance-report.md](docs/acceptance-report.md).

| Сборка | Как получить | Что умеет |
| --- | --- | --- |
| Core | `dotnet build -c Release` | Профили, изоляция, жизненный цикл, настройки, напоминания, импорт/экспорт, сеть System. Профили с прокси заблокированы |
| Experimental proxy | `dotnet build -c Release -p:ExperimentalProxy=true` | Core + HTTP-прокси через флаг `--proxy-server`. Помечена в интерфейсе как экспериментальная, сетевая изоляция не гарантируется |
| Production candidate с прокси | — | Не заявляется: документированного per-profile proxy API в WebView2 нет, см. [ADR-001](docs/ADR-001-browser-engine.md) |

## Требования

- Windows 11 x64.
- .NET 10 Desktop Runtime (или публикация self-contained, см. ниже).
- Microsoft Edge WebView2 Runtime (Evergreen). Приложение проверяет его при запуске и предлагает страницу установки.
- Для разработки: .NET SDK 10.0.1xx.

## Сборка и тесты

```powershell
./build.ps1                       # restore, build Core + Experimental, тесты ядра
./build.ps1 -Publish              # + self-contained win-x64 в artifacts/
dotnet test tests/ProtonProfiles.Core.Tests
```

На Linux/macOS собирается только для проверки компиляции (`EnableWindowsTargeting`); это не проверка поведения WebView2.

## Структура

```
src/ProtonProfiles.Core     платформенно-независимое ядро (net10.0)
  Model/                    ProfileConfig, ProxyEndpoint, перечисления
  Validation/               общие правила UI и импорта
  Lifecycle/                ProfileLifecycleService, IBrowserEngine, лимит окружений
  Persistence/              SQLite со схемой версий, миграции с резервной копией
  Storage/                  пути из UUID, межпроцессная блокировка, безопасное удаление
  Interchange/              JSON-обмен настройками v1
  Network/                  готовность сети, флаг прокси, сопоставление 407-запросов
  Permissions/ Navigation/ Downloads/ Reminders/ Diagnostics/
src/ProtonProfiles.App      WPF + WebView2 (net10.0-windows), интерфейс на русском
tests/ProtonProfiles.Core.Tests   161 тест (xUnit)
tests/fixture               HTTPS-стенд изоляции на двух origin
schema/                     JSON Schema обмена и пример без секретов
docs/                       ADR-001, отчёт по приёмке, манифест возможностей
```

## Главные решения

- Путь данных: `%LOCALAPPDATA%\ProtonProfiles\Profiles\<UUID>\WebViewData`, только из UUID. Блокировки — в `Locks\`, вне удаляемой папки.
- Инициализация строго по §4.5: окружение с опциями → проверка фактического UDF и канала → controller options (постоянный профиль, `ScriptLocale`) → обработчики и настройки → навигация на `https://mail.proton.me/`.
- Закрытие ждёт `BrowserProcessExited` до 15 с, иначе «Требуется восстановление»; UDF не удаляется и чужие процессы не завершаются.
- Не больше трёх живых окружений; при открытии четвёртого пользователь выбирает, что закрыть.
- Сброс сессии и удаление профиля — локальные действия; они не удаляют аккаунт Proton и не отзывают его сеансы.
- Пароли и автозаполнение браузера отключены; разрешения хранятся в одной политике приложения (`SavesInProfile = false`).
- Секреты прокси — в диспетчере учётных данных Windows; каждый новый секрет получает новую ссылку.
- Напоминания — локальные отметки «Я проверил ящик», по умолчанию раз в 6 месяцев. Proton рекомендует входить в бесплатный аккаунт хотя бы раз в год (сведения на 3 октября 2026).

## Ограничения

- Разные UA, языки и IP не делают аккаунты несвязываемыми; часовой пояс, Canvas, WebGL, шрифты и т. п. не настраиваются.
- Скрытый профиль продолжает работать в сети. Уведомления приходят только от открытых профилей.
- Аутентификация Basic на сайтах (401) отменяется: учётные данные прокси никогда не отдаются сайтам.
- Установщик и подпись не подготовлены (этап 3).
- Отладочная сборка позволяет направить профили на тестовый стенд переменными `PP_FIXTURE_ORIGINS` и `PP_FIXTURE_START`; Release их игнорирует.
