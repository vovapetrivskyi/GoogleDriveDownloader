# Google Drive Downloader

Windows-застосунок для завантаження приватних файлів Google Drive за посиланнями з XLSX. Повні вимоги містяться в [SPEC.md](SPEC.md), послідовність реалізації та результати перевірок — у [TASKS.md](TASKS.md).

Реалізовано лише етап 1: solution, WinForms-проєкт, окремий xUnit-проєкт і залежності. Застосунок відкриває початкове порожнє вікно. Авторизація, читання XLSX і завантаження ще не реалізовані.

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
- `tests/GoogleDriveDownloader.Tests` — xUnit-проєкт із посиланням на застосунок. Тестових сценаріїв на етапі 1 ще немає.

Не додавайте до Git OAuth credentials, токени, приватні документи або конфігурацію з реальними секретами. Імпортований OAuth JSON, токени, журнали, тимчасові файли та артефакти виключені через `.gitignore`. Зовнішній `appsettings.json` буде доданий на етапі 2 з порожніми credentials.
