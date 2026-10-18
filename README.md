# Google Drive Downloader

Windows-застосунок для завантаження приватних файлів Google Drive за посиланнями з XLSX. Повні вимоги містяться в [SPEC.md](SPEC.md), послідовність реалізації та результати перевірок — у [TASKS.md](TASKS.md).

Реалізовано етапи 1–2: структура solution, зовнішня конфігурація, DI та фонове журналювання. Головне вікно повідомляє про відсутні OAuth credentials і недоступність файлового журналу. Авторизація, читання XLSX, завантаження та повний інтерфейс ще не реалізовані.

## Розробка

Потрібні Windows x64 та .NET SDK 10.0.402 або новіший patch тієї самої SDK feature band відповідно до `global.json`. Для роботи через IDE потрібна її версія з підтримкою .NET 10 і WinForms.

```powershell
dotnet restore GoogleDriveDownloader.sln --locked-mode
dotnet build GoogleDriveDownloader.sln --no-restore
dotnet test GoogleDriveDownloader.sln --no-build --no-restore
dotnet run --project src/GoogleDriveDownloader --no-build --no-restore
```

У поточному середовищі SDK завантажено локально в `.tools/dotnet`; системні SDK не змінено. Щоб використовувати його в поточному сеансі PowerShell:

```powershell
$env:DOTNET_ROOT = (Resolve-Path .tools/dotnet).Path
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
```

Каталог `.tools` не входить до Git; після клонування потрібно встановити SDK окремо. Версії прямих залежностей зафіксовані в проєктах, транзитивних — у `packages.lock.json`. Після навмисної зміни залежностей слід виконати restore без `--locked-mode` та перевірити зміни lock-файлів.

## Структура

- `src/GoogleDriveDownloader` — єдиний виробничий WinForms-проєкт: `Presentation`, `Application`, `Infrastructure`.
- `tests/GoogleDriveDownloader.Tests` — 38 локальних перевірок конфігурації, DI та журналювання; Google credentials і мережа для запуску тестів не потрібні.

## Конфігурація

Зовнішній `appsettings.json` розташований поруч із EXE, копіюється при збірці та залишається зовнішнім при публікації. Програма читає його відносно `AppContext.BaseDirectory`, незалежно від робочої директорії. Зміни застосовуються після перезапуску.

| Налаштування | Значення за замовчуванням і допустимі значення |
|---|---|
| `GoogleOAuth:ClientId`, `ClientSecret` | Обидва порожні; це нормальний запуск без авторизації. Підтримується заповнення обох полів даними власного Desktop Client |
| `GoogleOAuth:Scope` | Лише `https://www.googleapis.com/auth/drive.readonly` |
| `Download:MaxConcurrentDownloads` | Лише `1` у MVP |
| `Download:RetryCount` | `3`, допустимо `0–3` повторів після початкової спроби |
| `Download:RequestTimeoutSeconds` | `100`, допустимо `1–3600` секунд на HTTP-запит |
| `Download:AuthorizationTimeoutSeconds` | `300`, допустимо `1–3600` секунд |
| `Download:DuplicatePolicy` | Лише `Rename`: суфікси без перезапису |
| `Export:DocsMimeType`, `SheetsMimeType`, `SlidesMimeType` | Лише MIME формату DOCX, XLSX, PPTX відповідно, наведені у файлі конфігурації |
| `Logging:MinimumLevel` | `Information`; також `Warning` або `Error`, без урахування регістру |
| `Logging:RetentionDays` | `30`, допустимо `1–3650` днів |

Для відсутніх окремих параметрів застосовуються наведені значення за замовчуванням. Некоректні значення, частково заповнені credentials, відсутній або пошкоджений файл спричиняють зрозуміле повідомлення та коректне завершення запуску. Вміст JSON і значення секретів у повідомлення про помилку не включаються. Імпорт OAuth JSON і захищене зберігання токенів належать до наступних етапів.

## Журналювання

Логи Serilog зберігаються у `%LOCALAPPDATA%\GoogleDriveDownloader\logs\log-yyyyMMdd.jsonl`. Кожний рядок — JSON-подія з часом, рівнем, повідомленням, номером рядка XLSX, назвою документа, File ID, статусом, HTTP-кодом і очищеною діагностикою винятку, якщо вони надані. Ротація щоденна; старі файли прибираються за віком під час відкриття чергового журналу.

Запис відбувається у фоновому потоці. Відомі OAuth credentials, іменовані токени, паролі й Bearer-значення маскуються перед передаванням як у Serilog, так і в події `ILoggingService.EntryLogged`. UI-підписники мають використовувати `IProgress<LogEntry>`, створений у UI-потоці. Фільтр мінімального рівня діє для обох виходів.

Якщо файл недоступний або запис неповний, програма показує попередження й припиняє файлове журналювання до перезапуску; підписники продовжують отримувати події. Це службове попередження надходить незалежно від мінімального рівня. При звичайному закритті DI-контейнер асинхронно завершує чергу та звільняє файл.

Не додавайте до Git OAuth credentials, токени, приватні документи або конфігурацію з реальними секретами. Імпортований OAuth JSON, токени, журнали, тимчасові файли та артефакти виключені через `.gitignore`; у репозиторному `appsettings.json` credentials повинні залишатися порожніми.
