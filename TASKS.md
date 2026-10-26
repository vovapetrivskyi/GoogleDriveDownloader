# План реалізації Google Drive Downloader для TASKS.md

## 1. Стан і висновки аналізу

Повністю прочитано `SPEC.md`, `AGENTS.md` та цей план. Початковий стан: лише документи вимог і планування, без solution, програмного коду й тестів.

**Останній завершений етап:** етап 10 — повна локальна перевірка й документація (18.10.2026). Restore з lock-файлами та build успішні; 438 тестів пройдено. Ручні Google-сценарії явно залишено відкритими без наданих реальних credentials. Етап 11 не розпочато.

**Додаткове завдання від 26.10.2026:** завершено налаштування назв стовпців XLSX через appsettings.json; поточна збірка без помилок і попереджень, 455 тестів пройдено. Подробиці — у розділі 5. Етап 11 не розпочато.

Затверджена архітектура придатна для MVP: один WinForms-проєкт із логічними шарами Presentation, Application, Infrastructure та окремий тестовий проєкт. Змінювати її не потрібно.

| Проблема або ризик | Рішення в плані |
|---|---|
| Системно встановлено лише SDK .NET 6 і 9 | На етапі 1 підготовлено локальний SDK 10.0.402 у `.tools/dotnet`; системні SDK не змінено. Архів перевірено за офіційним SHA-512 |
| Google SDK має кілька перевантажень авторизації | Використати перевантаження з явним `usePkce: true` і власним захищеним `IDataStore`; перевірити сигнатуру встановленої версії. [Документація Google](https://docs.cloud.google.com/dotnet/docs/reference/Google.Apis/latest/Google.Apis.Auth.OAuth2.GoogleWebAuthorizationBroker) |
| Завантажувач SDK може повернути помилку через результат, без звичайного винятку | Перед збереженням перевіряти `DownloadStatus.Completed` та `Exception`. [MediaDownloader](https://docs.cloud.google.com/dotnet/docs/reference/Google.Apis/latest/Google.Apis.Download.MediaDownloader) |
| HTTP 403 не завжди означає відсутність доступу; 404 не дозволяє точно відрізнити видалений файл від недоступного | Класифікувати помилки за HTTP-кодом і Google reason; повторювати тимчасові обмеження частоти, показувати коректне повідомлення для 404. [Помилки Drive API](https://developers.google.com/workspace/drive/api/guides/handle-errors) |
| `files.export` обмежує результат 10 МБ | Показувати помилку конкретного запису та видаляти `.part`; `files.download` залишити майбутнім розширенням. [Завантаження й експорт](https://developers.google.com/workspace/drive/api/guides/manage-downloads) |
| Читання `IXLCell.Value` може обчислювати формулу | Читати збережені значення без перерахунку; не викликати методи обчислення чи збереження книги. [ClosedXML](https://docs.closedxml.io/en/latest/concepts/formula-calculation.html) |
| Перевірка існування імені перед записом не усуває колізію з іншим процесом | Остаточне перейменування виконувати без перезапису; при колізії повторно обирати суфікс |
| Single File має особливості визначення шляхів і залежностей | Використовувати `AppContext.BaseDirectory`; перевірити саме опублікований EXE. [Документація .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) |
| Формулювання «Windows 10/11» ширше за актуальну офіційну підтримку ОС | Перевірити роботу на цільових Windows 10/11 x64 і окремо документувати підтримувані Microsoft редакції та версії. [Підтримка Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows) |
| `drive.readonly` є Restricted Scope; External / Testing має обмеження строку refresh token | Включити налаштування consent screen, test users, повторну авторизацію та обмеження поширення в README. [Scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [строк дії токенів](https://developers.google.com/identity/protocols/oauth2#expiration) |

## 2. Архітектура, залежності й узгоджені правила

- **Presentation:** `MainForm`, діалоги вибору файлів та OAuth JSON, відображення стану, прогресу й журналу. Бізнес-логіка не звертається до WinForms-контролів.
- **Application:** читання й перевірка XLSX, розбір URL, визначення імен, послідовна пакетна обробка та підсумки.
- **Infrastructure:** Google OAuth, DPAPI, Drive API, файлове збереження та Serilog.
- **Контракти:** замінюваний `IGoogleDriveService` для метаданих і передачі вмісту в `Stream`; інтерфейси авторизації та файлового збереження лише там, де потрібні ізоляція або тестування.
- **Моделі:** вхідний запис із номером рядка, результат валідації, посилання з File ID і resource key, метадані, `DownloadResult`, `DownloadProgress` і підсумок пакета. Моделі не містять SDK-типів або WinForms-типів.
- **Залежності застосунку:** передбачені SPEC пакети Google, ClosedXML, Configuration.Json, DependencyInjection, Serilog, Serilog.Sinks.File. API `System.Security.Cryptography.ProtectedData` для DPAPI вже доступний через цільовий WindowsDesktop framework .NET 10; окремий пакет не потрібен (перевірено під час етапу 1, попередження SDK NU1510). Для тестів — xUnit, адаптер і тестовий SDK.
- Версії обрати серед стабільних, сумісних із .NET 10, зафіксувати точними номерами й lock-файлами на етапі 1. Узгодити версії Google-пакетів із транзитивними вимогами. За метаданими [Google Drive SDK](https://www.nuget.org/packages/Google.Apis.Drive.v3/) і [ClosedXML](https://www.nuget.org/packages/ClosedXML/) цільові платформи сумісні; фактичну збірку й Single File перевірити окремо.

Прийняті правила MVP:

- Одне активне завантаження; без відновлення пакетів, паралельності, обходу обмежень і режиму перезапису.
- Scope — лише `drive.readonly`; формати експорту — DOCX, XLSX, PPTX; дублікати — суфікси `(2)`, `(3)`.
- Налаштування цих можливостей присутні в конфігурації, але значення поза підтримуваними MVP відхиляються зрозумілою помилкою.
- Імпортована OAuth-конфігурація має пріоритет над credentials із `appsettings.json`; решта налаштувань надходить із зовнішнього `appsettings.json`. Зміна OAuth-конфігурації анулює локальну авторизацію.
- Заголовки містяться в першому непорожньому рядку першого аркуша; пробіли навколо заголовків ігноруються. Відсутні або дубльовані обов’язкові заголовки — помилка книги. За додатковим запитом від 26.10.2026 назви задаються Excel:NameColumnHeader і Excel:LinkColumnHeader; значення за замовчуванням — «Назва» та «Посилання».
- Справжнє гіперпосилання має пріоритет над текстом комірки. Формули не виконуються: дозволено використати збережений результат, якщо він придатний; інакше — помилка рядка. Обчислення `HYPERLINK()` не входить до MVP.
- Порожні рядки не входять до загальної кількості записів. Некоректні непорожні рядки враховуються як помилкові. Необроблені записи після зупинки пакета враховуються як пропущені.
- Папки, shortcuts та інші не передбачені SPEC Workspace-типи отримують зрозумілу помилку запису.

## 3. Послідовні етапи реалізації

Етапи 1–10 мають статус **«Завершено»** в межах доступних перевірок; ручні Google-сценарії етапу 10 залишаються відкритими. Етап 11 — **«Не розпочато»**. Кожен наступний етап виконується лише за окремим запитом користувача.

### Етап 1. Середовище, solution і залежності

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Перевірити наявність SDK .NET 10; за відсутності системного SDK підготувати локальний SDK у виключеному з Git каталозі `.tools/dotnet`.
2. [x] Створити solution, один WinForms-проєкт і окремий тестовий проєкт із посиланням на застосунок.
3. [x] Увімкнути `net10.0-windows`, C# 14, nullable reference types; створити логічні каталоги шарів.
4. [x] Підключити й зафіксувати необхідні пакети; виключити з контролю версій секрети, локальні дані й артефакти.

**Результат:** створено solution з єдиним WinForms-застосунком і окремим xUnit-проєктом; початкове вікно без функціональних контролів, каталоги Presentation/Application/Infrastructure, налаштування C# 14, nullable та x64. Версії пакетів і транзитивних залежностей зафіксовані; джерело пакетів — nuget.org. Git ініціалізовано на гілці `main`, коміт не створювався.

**Критерії готовності:** виконано — restore і build успішні; xUnit розпізнає тестову збірку; немає зайвих виробничих проєктів чи реальних credentials.

**Перевірки:** усі команди виконані з кореня репозиторію через `.tools/dotnet/dotnet.exe` (SDK 10.0.402). Для процесів перевірки `DOTNET_ROOT` і `DOTNET_ROOT_X64` вказували на локальний SDK, `DOTNET_CLI_HOME` та `NUGET_PACKAGES` — на підкаталоги `.tools`.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln` | Успішно для обох проєктів. Початкове NU1510 усунено видаленням зайвого PackageReference ProtectedData; повторний restore без попереджень |
| `dotnet build GoogleDriveDownloader.sln --no-restore` | Успішно, Debug; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity normal` | Код завершення 0; xUnit VSTest Adapter 4.0.1 розпізнав тестову збірку на .NET 10.0.13 x64; знайдено 0 тестів |
| `dotnet restore GoogleDriveDownloader.sln --locked-mode` | Успішно для обох проєктів; перевірено відтворення залежностей за lock-файлами |
| `git check-ignore --no-index -v ...` | Перевірено виключення `.tools`, `bin`, `obj`, credentials, токенів, журналів, завантажень, `.part` і `dist` |

**Створені файли:**

- Корінь: `.gitattributes`, `.gitignore`, `global.json`, `Directory.Build.props`, `NuGet.Config`, `GoogleDriveDownloader.sln`, `README.md`.
- `src/GoogleDriveDownloader/`: `GoogleDriveDownloader.csproj`, `packages.lock.json`, `Program.cs`, `Presentation/MainForm.cs`, `Presentation/MainForm.Designer.cs`, `Application/.gitkeep`, `Infrastructure/.gitkeep`.
- `tests/GoogleDriveDownloader.Tests/`: `GoogleDriveDownloader.Tests.csproj`, `packages.lock.json`.

**Змінені файли:** `TASKS.md` — актуалізовано початковий стан, статус і результати етапу 1. `SPEC.md` та `AGENTS.md` не змінено.

**Обмеження:** SDK, кеші й артефакти у `.tools`/`bin`/`obj` локальні та не входять до Git; після клонування потрібен SDK відповідно до `global.json`. Тестових сценаріїв ще немає, тому запуск test не означає перевірку бізнес-логіки. OAuth, конфігурація застосунку, логування, XLSX і завантаження не реалізовувалися; GUI вручну не запускався, publish не виконувався. Подальша робота — лише за окремим запитом.

### Етап 2. Конфігурація, DI й журналювання

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Реалізувати завантаження зовнішнього `appsettings.json` відносно EXE та перевірку налаштувань.
2. [x] Передбачити всі параметри SPEC: OAuth, concurrency, retry, тайм-аути, дублікати, експорт, рівень і строк зберігання логів.
3. [x] Налаштувати DI та коректне звільнення сервісів.
4. [x] Реалізувати узгоджені події для UI й Serilog, щоденну ротацію та зберігання за віком 30 днів.
5. [x] Захистити журнал від витоку credentials і токенів; відмова файлового sink не повинна закривати застосунок.

**Результат:** зовнішній файл налаштувань із порожніми OAuth credentials, типізовані налаштування та перевірка підтримуваних MVP значень. JSON читається поза UI-потоком, відносно EXE, без автоматичного перезавантаження. Відсутні параметри мають документовані значення за замовчуванням; помилки читання/валідації не розкривають значення чи фрагменти JSON.

DI-контейнер створює головну форму й володіє singleton-сервісом журналювання. Serilog записує JSONL у `%LOCALAPPDATA%\GoogleDriveDownloader\logs\`, щоденно змінює файл і застосовує строк зберігання за віком (типово 30 днів). Запис працює через фонову чергу; при закритті вона асинхронно завершується. UI та файл отримують однаково очищені події з часом, рівнем, контекстом рядка й діагностикою. Відмова файлового sink або стороннього підписника не зупиняє інші повідомлення. Про недоступний/неповний файловий журнал надходить безпечне попередження; файлове журналювання відключається до перезапуску.

До початкової форми додано лише повідомлення про відсутні credentials та відмову журналу через `IProgress<LogEntry>`. Повний інтерфейс із RichTextBox залишається етапом 9. Синхронні межі STA у точці входу використовуються до запуску message loop і після його завершення; під час роботи форми файлові операції журналювання не блокують UI.

**Критерії готовності:** виконано. Порожні credentials приймаються; пошкоджений JSON, дублікати ключів, неправильна структура та неприпустимі значення дають безпечні зрозумілі помилки. Файлове збереження, ротація, строк зберігання, очищення секретів, продовження подій при помилці sink та звільнення ресурсів перевірені локальними тестами.

**Перевірки:** команди виконані через локальний SDK `.tools/dotnet/dotnet.exe` 10.0.402 із `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_CLI_HOME` та `NUGET_PACKAGES`, налаштованими як на етапі 1.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode` | Успішно для обох проєктів; нові пакети не додавались, lock-файли не змінені |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Фінальна збірка Debug успішна; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | Успішно: 38 пройдено, 0 помилкових, 0 пропущених; Google credentials і мережа не потрібні |

Під час перевірок виправлено звільнення `IConfigurationRoot`, обробку `InvalidDataException` від JSON-провайдера й попередження аналізатора xUnit2031.

**Створені файли:**

- `src/GoogleDriveDownloader/`: `appsettings.json`, `Bootstrapper.cs`.
- `Application/Configuration/AppSettings.cs`; `Application/Logging/ILoggingService.cs`, `Application/Logging/LogEntry.cs` у виробничому проєкті.
- `Infrastructure/Configuration/ConfigurationLoader.cs`; `Infrastructure/Logging/LoggingService.cs`, `Infrastructure/Logging/SensitiveDataRedactor.cs` у виробничому проєкті.
- `tests/GoogleDriveDownloader.Tests/`: `ConfigurationLoaderTests.cs`, `LoggingServiceTests.cs`, `BootstrapperTests.cs`, `TestDirectory.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/GoogleDriveDownloader.csproj`, `src/GoogleDriveDownloader/Program.cs`, `src/GoogleDriveDownloader/Presentation/MainForm.cs`, `README.md`, `TASKS.md`.

**Обмеження:** GUI вручну не запускався; запуск/закриття вікон і повідомлення потребують ручної перевірки на Windows. Локальні тести використовують тимчасові каталоги, не створюють журналів у профілі користувача та не звертаються до Google. Публікація Single File не виконувалась; для конфігурації встановлено `ExcludeFromSingleFile` і копіювання поряд із EXE. Імпорт OAuth, DPAPI, авторизація та решта етапів не реалізовувались.

### Етап 3. Імпорт OAuth credentials і захищене сховище

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Приймати стандартний JSON із секцією Desktop Client `installed`; відхиляти Service Account, Web Client і неповні файли.
2. [x] Зберігати імпортований `oauth-client.json` у визначеному SPEC каталозі користувача.
3. [x] Реалізувати `IDataStore` з DPAPI `CurrentUser`, атомарним записом `auth.dat` і без незашифрованих тимчасових токенів.
4. [x] Прив’язувати сховище до відбитка OAuth-конфігурації та scope.
5. [x] Обробляти відсутній, пошкоджений або недоступний файл без аварійного завершення.

**Результат:** незалежний імпорт Desktop JSON через мінімальний UI та singleton-сервіс конфігурації. Перевіряються тип клієнта, обов'язкові поля, дублікати властивостей, Google endpoints і loopback redirect; підтримується UTF-8 BOM. Нормалізований `oauth-client.json` зберігається у LocalAppData, джерело не змінюється. Імпорт має пріоритет над резервною парою credentials; несправний імпортований файл не спричиняє мовчазного переходу на інший клієнт.

`DpapiTokenStore` реалізує фактичний контракт `IDataStore` із Google SDK 1.77.0 та використовує його Newtonsoft-серіалізатор. Весь кеш у `auth.dat` шифрується DPAPI `CurrentUser` із додатковою ентропією — SHA-256 від ідентичності клієнта, секрету, scope та призначення сховища. Є окремі типізовані ключі, видалення, очищення та додаткові перевантаження зі скасуванням. Файлові операції й DPAPI працюють поза UI-потоком, доступ до кеша та імпорту синхронізовано. Тимчасовий файл токенів містить лише ciphertext; заміна атомарна, після помилки він видаляється.

Після імпорту кеш очищується, а попередні екземпляри сховища не можуть записувати чи видаляти токени нового клієнта. Відсутній кеш — нормальний стан; недоступний або непридатний не повертає токенів. Непридатний кеш видаляється за можливості; повідомлення не містять вихідних винятків, токенів чи JSON. Нові імпортовані credentials реєструються в потокобезпечному механізмі маскування журналу. Розміри файлів обмежено 1 МіБ.

**Технічне уточнення етапу 2:** перевірка повноти резервної пари OAuth перенесена із загального JSON-loader до вибору ефективної OAuth-конфігурації. Це необхідно, щоб неповний `appsettings.json` не блокував коректний імпорт. Решта валідації загальних налаштувань збережена.

**Критерії готовності:** виконано. Перевірені справжнє DPAPI-шифрування, відновлення SDK `TokenResponse` між екземплярами сервісу, незалежні типізовані ключі, скидання при зміні кожного компонента прив'язки, блокування застарілого сховища, пошкоджені/недоступні файли, скасування, паралельні запити до одного кеша, збереження попереднього файла при невдалій атомарній заміні та відсутність секретів у журналі.

**Перевірки:** локальний SDK `.tools/dotnet/dotnet.exe` 10.0.402, налаштування середовища як на попередніх етапах; без нових пакетів або змін lock-файлів.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --verbosity minimal` | Успішно, залежності обох проєктів актуальні |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Фінальна збірка Debug успішна; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | Під завантаженим Windows-профілем: 81 пройдено, 0 помилкових, 0 пропущених |

Початковий запуск DPAPI-тестів у sandbox був неуспішним через недоступний профіль sandbox-користувача. Фінальні перевірки виконано поза sandbox під поточним Windows-користувачем; шифрування не підмінювалося, Google не використовувався. Усі auth-файли тестів створювались лише у власних тимчасових каталогах.

**Створені файли:**

- `src/GoogleDriveDownloader/Infrastructure/Authentication/`: `AuthFile.cs`, `AuthStorageException.cs`, `OAuthDesktopJson.cs`, `OAuthConfigurationService.cs`, `DpapiTokenStore.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `AuthTestScope.cs`, `OAuthConfigurationServiceTests.cs`, `DpapiTokenStoreTests.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `Application/Logging/ILoggingService.cs`, `Infrastructure/Configuration/ConfigurationLoader.cs`, `Infrastructure/Logging/LoggingService.cs`, `Infrastructure/Logging/SensitiveDataRedactor.cs`, `Presentation/MainForm.cs`, `Presentation/MainForm.Designer.cs` у виробничому проєкті; `tests/GoogleDriveDownloader.Tests/ConfigurationLoaderTests.cs`, `README.md`, `TASKS.md`.

**Обмеження:** UI вручну не запускався; ручне перенесення файла між різними Windows-акаунтами не перевірялось. Налаштовані credentials і наявний кеш не вважаються підтвердженням авторизації: UI залишається у стані «Не авторизовано». Вхід у браузері, PKCE, оновлення/відкликання токенів і решта OAuth flow належать до етапу 4 та не реалізовані. Публікація не виконувалась.

### Етап 4. Google OAuth

**Статус:** завершено 18.10.2026; ручні сценарії Google залишаються невиконаними через відсутність реальних credentials.

**Кроки:**

1. [x] Перевірити фактичні сигнатури встановленого SDK.
2. [x] Реалізувати Authorization Code Flow із PKCE, системним браузером, loopback receiver і offline access.
3. [x] Забезпечити автоматичне оновлення access token.
4. [x] При недійсному refresh token очистити недійсний стан і запросити повторний вхід без нескінченних повторів.
5. [x] Реалізувати локальний вихід, очищення токенів і вибір іншого акаунта.
6. [x] Підтримати скасування, відмову користувача та обмежений час очікування авторизації.

**Результат:** реалізовано GoogleAuthService зі збереженням затвердженої архітектури та залежностей. Перевірено XML-документацію встановлених Google-пакетів 1.77.0 і [відповідний вихідний код Broker](https://github.com/googleapis/google-api-dotnet-client/blob/v1.77.0/Src/Support/Google.Apis.Auth/OAuth2/GoogleWebAuthorizationBroker.cs). Використано фактичне перевантаження GoogleWebAuthorizationBroker.AuthorizeAsync з GoogleAuthorizationCodeFlow.Initializer, явним usePkce: true, DpapiTokenStore і SDK LocalServerCodeReceiver. PKCE S256 та обмін коду виконує SDK; scope лише drive.readonly, offline access, consent/select_account та loopback 127.0.0.1 із випадковим портом. Декоратор receiver додатково звіряє випадковий state; власний auth-сервер не створювався.

Відновлення при запуску не відкриває браузер. Google UserCredential оновлює access token перед запитами й зберігає його у DPAPI; при invalid_grant сервіс очищає сесію та пропонує повторний вхід. Явний вхід допускає одну інтерактивну спробу після непридатного кеша, без рекурсії. Сервіс надає SDK initializer для майбутнього Drive-адаптера: Bearer, автоматичне оновлення, обмежений повтор після 401 і прив'язка HTTP-запиту до сесії, що запобігає використанню нового акаунта для старого запиту.

Локальний вихід і зміна акаунта очищають пам'ять і auth.dat та змінюють покоління кеша, блокуючи пізні записи попереднього credential. Помилка видалення явно повідомляється й блокує повторне використання кеша в поточному процесі до успішного очищення. Підтримано CancellationToken, тайм-аути очікування та HTTP, відмову користувача, перевірку offline-токена, безпечні повідомлення без сирих OAuth-винятків. HTTP-клієнти звільняються також після помилки Broker. У мінімальному UI доступні вхід, вихід, зміна акаунта й скасування; статус оновлюється через IProgress, закриття скасовує активну операцію й асинхронно чекає на очищення.

**Критерії готовності:** локальні критерії виконано. Додано 25 перевірок із фактичним SDK та підмінами HTTP/receiver: PKCE challenge/verifier, state, scope, offline access, захищене збереження та відновлення між запусками, оновлення після закінчення токена, збереження refresh token, invalid_grant, межа інтерактивних спроб, HTTP 401, вихід, зміна акаунта, недоступний кеш, зміна конфігурації під час обміну коду, скасування, тайм-аут, закриття сервісу, відмова користувача й нерозкриття секретів. Реальна авторизація не підміняється цими перевірками; ручний сценарій наведено у README і явно позначено невиконаним.

**Перевірки:** локальний SDK .tools/dotnet/dotnet.exe 10.0.402, ті самі DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME та NUGET_PACKAGES, що на попередніх етапах. Нових пакетів немає, lock-файли не змінено.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --verbosity minimal` | Успішно для обох проєктів |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Фінальна збірка Debug успішна; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | Під завантаженим Windows-профілем: 106 пройдено, 0 помилкових, 0 пропущених |
| `git diff --check` | Успішно |

Тести виконано поза sandbox під поточним Windows-профілем через вимоги справжнього DPAPI. Google credentials, браузер, loopback listener і зовнішня мережа не використовувались. Тестові auth-файли створювались лише у власних тимчасових каталогах. Під час реалізації виправлено виявлені помилки компіляції та один тест журналювання; наведено фінальні результати всього набору.

**Створені файли:**

- `src/GoogleDriveDownloader/Infrastructure/Authentication/`: `GoogleAuthService.cs`, `AuthenticationException.cs`, `StateCodeReceiver.cs`, `OAuthHttpClientFactory.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `GoogleAuthServiceTests.cs`, `GoogleAuthLifecycleTests.cs`, `OAuthTestTransport.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `Infrastructure/Authentication/OAuthConfigurationService.cs`, `Presentation/MainForm.cs`, `Presentation/MainForm.Designer.cs` у виробничому проєкті; `README.md`, `TASKS.md`. SPEC.md та AGENTS.md не змінено.

**Обмеження:** GUI вручну не запускався; вхід через системний браузер із реальним Desktop Client, повторний запуск із реальним Google-кешем, оновлення/відкликання реальних токенів і зміна реального акаунта не перевірялись через відсутність credentials. Локальний вихід не відкликає дозвіл у Google і не виходить із браузера. Одночасна робота кількох процесів з одним профілем не координується. Публікація не виконувалась. Етапи 5–11 не розпочато.
### Етап 5. XLSX і Google Drive URL

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Реалізувати читання першого аркуша, пошук заголовків і збереження фізичних номерів рядків.
2. [x] Підтримати текстові URL та гіперпосилання, ігнорування порожніх рядків і незалежні помилки записів.
3. [x] Не обчислювати формули й не зберігати книгу. Синхронне читання ClosedXML виконувати поза UI-потоком.
4. [x] Розбирати всі шість обов’язкових форм URL та `resourcekey`.
5. [x] Перевіряти HTTPS, точні дозволені домени й структуру URL; відхиляти сторонні домени та неоднозначні File ID.

**Результат:** додано ExcelReader і DriveLinkParser у логічний шар Application/Input та зареєстровано в DI. Моделі DriveLink, InputRow і ExcelReadResult не залежать від ClosedXML, Google SDK чи WinForms. Результат зберігає впорядковані валідні й помилкові непорожні записи з фізичним номером рядка, назвою, File ID, окремим resource key та списком зрозумілих помилок. Помилки окремих записів не переривають читання.

Заголовки читаються з першого непорожнього рядка першого аркуша, незалежно від активної вкладки; регістр і пробіли ігноруються, порядок і номери стовпців довільні. Відсутні/дубльовані заголовки, порожній перший аркуш та пошкоджений XLSX дають безпечну помилку книги. Порожні, лише пробільні й лише відформатовані рядки пропускаються. Зайві стовпці не змінюють вибір обов'язкових; непорожній рядок лише в зайвих стовпцях залишається помилковим записом.

Target справжнього зовнішнього гіперпосилання має пріоритет над текстом комірки. Внутрішній або непридатний target не підміняється текстовим URL. Формули не виконуються: LoadOptions.RecalculateAllFormulas=false, читання лише CachedValue, без Value/GetString/GetFormattedString або збереження книги. Придатний збережений результат приймається, відсутній/порожній чи помилковий стає помилкою запису. Поведінку звірено з XML-документацією встановленого ClosedXML 0.105.1 та [документацією CachedValue](https://docs.closedxml.io/en/latest/concepts/formula-calculation.html). У тестах кешовані формули підготовлено прямою зміною OOXML, без їх обчислення.

Парсер підтримує всі шість URL зі SPEC, а також ті самі шляхи без кінцевої дії та з view/edit/preview/copy, trailing slash, query та fragment. Перевіряє HTTPS, точні домени, стандартний порт, відсутність userinfo, структуру вихідного шляху та допустимі символи ID. Відхиляє підроблені домени, папки, зайві сегменти, dot-segments, повторні id/resourcekey та різні ID у шляху й query. Resource key декодується один раз і зберігається для подальшого Drive-адаптера; мережевих запитів немає. Секрети, URL і фрагменти приватних файлів до повідомлень помилок не включаються.

**Критерії готовності:** виконано. Додано 117 локальних перевірок: обов'язкові й додаткові URL, resourcekey, кодування, неоднозначні параметри, підроблені домени, порядок/регістр заголовків, фізичні номери, перший аркуш, гіперпосилання та їх пріоритет, незалежні помилки, формули без кеша й із кешем, Excel error, порожні/пошкоджені книги, блокування файла, скасування до початку читання. Перевірено побайтову незмінність та час модифікації вхідного read-only XLSX, звільнення файлових дескрипторів, повторне й паралельне читання. Доповнено перевірку DI.

**Перевірки:** локальний SDK .tools/dotnet/dotnet.exe 10.0.402, налаштування DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME та NUGET_PACKAGES як на попередніх етапах. Залежності й lock-файли не змінено.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --verbosity minimal` | Успішно, залежності обох проєктів актуальні |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Успішно, Debug; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | 223 пройдено, 0 помилкових, 0 пропущених; у тому числі 117 нових перевірок етапу 5 і 106 попередніх |
| `git diff --check` | Успішно |

Повний набір тестів виконано під завантаженим Windows-профілем поза sandbox для попередніх DPAPI-тестів. Нові тести XLSX/URL також запускались у sandbox без DPAPI. Мережа, Office й реальні Google credentials не використовувались; XLSX створювались у власних тимчасових каталогах. Виявлений під час першої збірки тестів конфлікт імен SaveOptions усунено явним ClosedXML.Excel.SaveOptions.

**Створені файли:**

- `src/GoogleDriveDownloader/Application/Input/`: `DriveLink.cs`, `DriveLinkException.cs`, `DriveLinkParser.cs`, `ExcelReadException.cs`, `ExcelReadResult.cs`, `ExcelReader.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `DriveLinkParserTests.cs`, `ExcelReaderTests.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `tests/GoogleDriveDownloader.Tests/BootstrapperTests.cs`, `README.md`, `TASKS.md`. SPEC.md та AGENTS.md не змінено.

**Обмеження:** синхронний початковий loader ClosedXML не приймає CancellationToken; він працює у Task.Run, перевірки скасування виконуються до/після завантаження та під час обходу рядків і заголовків. Формули зі старим кешем навмисно не перераховуються. GUI вручну не запускався; підключення вибору XLSX до UI належить до етапу 9. Наявність файла й дозвіл на завантаження в Google не перевіряються парсером; Drive API належить до етапу 6. Етапи 6–11 не розпочато.

### Етап 6. Метадані, завантаження й експорт Drive

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Реалізувати `IGoogleDriveService` і Google-адаптер.
2. [x] Запитувати необхідні метадані: ім’я, MIME, розширення, розмір, стан видалення й `capabilities.canDownload`.
3. [x] Передавати resource key через передбачений Google заголовок; використовувати `supportsAllDrives` там, де метод підтримує його.
4. [x] Потоково завантажувати звичайні файли та експортувати Docs/Sheets/Slides у встановлені формати.
5. [x] Перевіряти кінцевий статус SDK; класифікувати помилки доступу, експорту й непідтримуваних типів.

**Результат:** додано SDK-незалежний контракт IGoogleDriveService, DriveFileMetadata, DriveTransferFormat та DriveServiceException у Application/Drive, адаптер GoogleDriveService у Infrastructure/Drive і реєстрацію singleton у DI. Затверджена архітектура й залежності збережені. Метадані запитуються з явним fields: id,name,mimeType,fileExtension,size,trashed,capabilities(canDownload); перевіряється відповідність File ID та повнота результату.

Тип передачі визначається за MIME метаданих. Звичайні файли передаються через files.get alt=media; Docs/Sheets/Slides — files.export у налаштовані DOCX/XLSX/PPTX. Кошик, відсутній canDownload, заборона завантаження, папки, shortcuts та інші Workspace-типи блокуються до запиту вмісту. supportsAllDrives=true передається в обидва files.get; export цього параметра не підтримує. Resource key застосовується до конкретного запиту метаданих/вмісту/експорту через X-Goog-Drive-Resource-Keys, без спільного стану заголовків.

Перевірено фактичні сигнатури встановлених Google-пакетів 1.77.0/1.77.0.4276 у XML-документації та [реалізацію MediaDownloader відповідної версії](https://github.com/googleapis/google-api-dotnet-client/blob/v1.77.0/Src/Support/Google.Apis/Download/MediaDownloader.cs). DownloadAsync перевіряє Completed, Exception, кількість записаних байтів та відомий розмір звичайного файла; Failed progress не стає успіхом. Передача використовує фіксований буфер близько 1 МіБ, не потребує Seek і не закриває потік викликача. Експорт понад 10 МБ має окрему помилку Google reason; додатково обмежено локальний запис експорту до 10 × 1024 × 1024 байтів.

Класифікація зберігає код помилки, HTTP-код, ознаку тимчасовості й Retry-After без сирого SDK-винятку або відповіді. Відрізняє rate-limit 403/429 від доступу, 404 відображає як «не знайдено або недоступний», підтримує тимчасові 500/502/503/504, мережу, тайм-аут, помилку цільового потоку й переповнення диска. CancellationToken діє також під час читання тіла після ResponseHeadersRead. Внутрішній backoff SDK вимкнений; повтори тимчасових помилок залишено етапу 8.

**Необхідне виправлення інтеграції OAuth:** остання дозволена SDK-спроба з HTTP 401 раніше пропускала очищення сесії через SupportsRetry=false. GoogleAuthService тепер скидає відхилену сесію й на цій межі; одне оновлення/повтор збережено, нескінченного циклу немає. Перевірено фактичним інтеграційним тестом Drive + OAuth на двох підмінених транспортах.

**Критерії готовності:** виконано. Додано 60 локальних перевірок: fields, supportsAllDrives, відсутність його в export, resource key для всіх типів запитів і відсутність змішування ключів, MIME трьох експортів, заборони, unsupported types, 403/404/429/5xx, обидва види Retry-After, не-JSON HTTP-помилки, exportSizeLimitExceeded, локальний ліміт, частковий вміст, порожній файл, некоректні метадані/ID, помилка цільового потоку й disk full, скасування, тайм-аут заголовків/тіла, звільнення HTTP-ресурсів, OAuth 401/repeated 401 та неавторизований запит. Синтетичне джерело понад 32 МіБ передається в потік без Seek; перший запис відбувається до прочитання всього джерела, буфер обмежений. Реальні credentials, мережа й Google Drive для тестів не потрібні.

**Перевірки:** локальний SDK .tools/dotnet/dotnet.exe 10.0.402, DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME та NUGET_PACKAGES як на попередніх етапах. Нових пакетів немає; lock-файли не змінено.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --verbosity minimal` | Успішно; залежності обох проєктів актуальні |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Успішно, Debug; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | 283 пройдено, 0 помилкових, 0 пропущених: 60 нових перевірок етапу 6 і 223 попередні |
| `git diff --check` | Успішно |

Повний набір виконано поза sandbox під завантаженим Windows-профілем для справжнього DPAPI у попередніх та інтеграційних OAuth-тестах. Усі HTTP-запити підмінено; браузер, мережа й реальні Google credentials не використовувались. Попередні незакомічені зміни етапу 5 збережено.

**Створені файли:**

- `src/GoogleDriveDownloader/Application/Drive/`: `IGoogleDriveService.cs`, `DriveFileMetadata.cs`, `DriveServiceException.cs`.
- `src/GoogleDriveDownloader/Infrastructure/Drive/`: `GoogleDriveService.cs`, `DriveRequestContext.cs`, `DriveErrorClassifier.cs`, `DriveDestinationStream.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `DriveTestTransport.cs`, `GoogleDriveServiceTests.cs`, `GoogleDriveStreamingTests.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `src/GoogleDriveDownloader/Infrastructure/Authentication/GoogleAuthService.cs`, `tests/GoogleDriveDownloader.Tests/BootstrapperTests.cs`, `README.md`, `TASKS.md`. SPEC.md та AGENTS.md не змінено.

**Обмеження:** реальні приватні файли, три Workspace-експорти, Shared Drives/resource key та обмеження Google вручну не перевірялись без credentials; GUI вручну не запускався. Потоком володіє викликач, після помилки/скасування в ньому може залишитися частковий вміст. Створення/видалення .part та остаточні імена належать до етапу 7, пакетна retry-політика — до етапу 8. files.download не реалізовано; стандартний export має ліміт. Етапи 7–11 не розпочато.

### Етап 7. Імена й безпечне файлове збереження

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Визначати розширення за форматом експорту або метаданими фактичного файла, а не URL.
2. [x] Усувати дублювання й виправляти неправильне розширення; зберігати змістовні крапки в основній назві.
3. [x] Обробляти заборонені символи, зарезервовані імена, кінцеві пробіли й крапки, довгі назви та path traversal.
4. [x] Створювати унікальний `.part` у вибраній директорії; після підтвердженого успіху закривати потік і перейменовувати без перезапису.
5. [x] Розв’язувати колізії з урахуванням регістру й повторної появи файла між перевіркою та перейменуванням.
6. [x] Видаляти власний незавершений файл після помилки або скасування; повідомляти про невдале очищення.

**Результат:** реалізовано FileNameResolver у Application/Storage, контракт IFileStorageService, SDK-незалежні коди FileStorageException і FileStorageService у Infrastructure/Storage; сервіси зареєстровано в DI. Архітектуру та залежності не змінено.

Розширення береться з формату Office-експорту або MIME/метаданих оригінального файла. Відомий MIME має пріоритет, невідомий використовує fileExtension чи початкове ім'я з метаданих; URL не використовується. Правильне розширення не дублюється, відомий неправильний суфікс замінюється. Незнайомі суфікси на кшталт .2025/.final зберігаються як основа; backup.tar.gz із правильним .gz не втрачає .tar. Заборонені Windows-символи, керівні символи й некоректні surrogate замінюються, порожня основа стає «Документ», зарезервовані назви отримують префікс. Враховано COM/LPT із надрядковими цифрами та повторну перевірку після скорочення. Unicode й коректні surrogate-пари зберігаються.

Збереження приймає callback підтвердженої передачі: створює власний .gdd-GUID.part через CreateNew у вибраній існуючій директорії, асинхронно записує, виконує FlushAsync, закриває потік і лише потім перейменовує. Кінцевий шлях перевіряється як безпосередній дочірній файл; нерозібрані шляхи, alternate streams та device names відхиляються. Імена обмежено 255 одиницями UTF-16, повний звичайний шлях — 259, із додатковим скороченням основи під директорію та суфікс дубліката. Надто довгий шлях має явну помилку. Рішення звірено з [правилами імен Windows](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file) та [контрактом File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0).

Колізії з файлами/директоріями обробляються без урахування регістру, додаються (2), (3) тощо. Фінальна операція завжди File.Move(overwrite:false); при появі іншого файла або папки між перевіркою й Move обирається наступний суфікс. Початкові файли не змінюються. Помилки передачі та скасування очищають лише власний .part; невдале видалення реєструється як Warning/PartialFileCleanupFailed з контекстом рядка без маскування початкової помилки. Розрізняються disk full, недоступність директорії та інші файлові відмови; сирі системні повідомлення не передаються до журналу/UI. I/O виконується поза UI-потоком.

**Критерії готовності:** виконано. Додано 90 перевірок назв і реальної файлової поведінки: розширення/експорт, змістовні крапки, Unicode, Windows devices, traversal/ADS, довгі імена й suffix budgets, дублікат у різному регістрі, колізії з папками та між перевіркою/Move, одночасні екземпляри без перезапису, закритий потік перед Move, скасування під час/після запису, помилка commit, disk full/unavailable destination, колізія CreateNew, cleanup failure без втрати початкової помилки й видалення лише власного .part. Google SDK із підміненим HTTP підтвердив: невдалий progress або невідповідність довжини не стають кінцевим файлом. Доповнено перевірку DI.

**Перевірки:** локальний SDK .tools/dotnet/dotnet.exe 10.0.402; DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME і NUGET_PACKAGES як на попередніх етапах. Нових пакетів і змін lock-файлів немає.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --verbosity minimal` | Успішно, залежності обох проєктів актуальні |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Успішно, Debug; 0 попереджень, 0 помилок |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal` | 373 пройдено, 0 помилкових, 0 пропущених: 90 нових перевірок етапу 7 і 283 попередні |
| `git diff --check` | Успішно |

Нові файлові тести запускались у sandbox; повний набір — під завантаженим Windows-профілем поза sandbox для справжнього DPAPI попередніх етапів. Використовувалися лише власні тимчасові каталоги; для помилок race/cleanup/disk full підмінялися окремі файлові операції, реальний диск не заповнювався. Мережа й Google credentials не потрібні. Початкове nullable-попередження MoveFile усунено використанням незмінного шляху створеного temporary.

**Створені файли:**

- `src/GoogleDriveDownloader/Application/Storage/`: `FileNameResolver.cs`, `IFileStorageService.cs`, `FileStorageException.cs`.
- `src/GoogleDriveDownloader/Infrastructure/Storage/`: `FileStorageService.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `FileNameResolverTests.cs`, `FileStorageServiceTests.cs`, `StorageTestDoubles.cs`, `StoredDriveDownloadTests.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `tests/GoogleDriveDownloader.Tests/BootstrapperTests.cs`, `README.md`, `TASKS.md`. SPEC.md та AGENTS.md не змінено.

**Обмеження:** звичайні шляхи обмежено 259 одиницями UTF-16 для сумісності з Windows Shell; надто довгу вибрану директорію треба скоротити, extended/device namespaces не підтримуються. Невідомий суфікс назви зберігається, бо його зміст неможливо надійно вивести з тексту. Невдале очищення або аварійне завершення процесу може залишити .part; автоматичного відновлення після аварії немає. Межею успішного завершення є File.Move: після нього результат не перетворюється на скасований. GUI, реальні Google-операції, збої SMB і вимкнення живлення вручну не перевірялися. Етапи 8–11 не розпочато.

### Етап 8. Пакетна обробка, retry і статистика

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Побудувати послідовний процес: валідація → метадані → формат → `.part` → передача → остаточне ім’я → результат.
2. [x] Передавати `CancellationToken` через усі асинхронні операції та затримки.
3. [x] Реалізувати до трьох повторів після початкової спроби: exponential backoff, jitter і `Retry-After`.
4. [x] Повторювати тимчасові мережеві помилки, 429, відповідні rate-limit 403 і тимчасові 5xx; узгодити політику з внутрішніми повторами SDK.
5. [x] Перед повторною передачею очищувати тимчасовий вміст, щоб уникнути дописування до невдалої спроби.
6. [x] Продовжувати після помилок окремих записів; зупиняти пакет при скасуванні, втраті авторизації, вичерпанні диска або недоступності цільової директорії.
7. [x] Повертати окремі стани завершення: нормальне, скасоване, критична помилка.

**Результат:** DI-сервіс `BatchDownloadService` читає XLSX або приймає готовий `ExcelReadResult` і виконує весь конвеєр поза UI-потоком, послідовно та із захистом від другого одночасного запуску. Кожний непорожній рядок має рівно один `DownloadResult`; `BatchDownloadResult` містить статус, підсумкове повідомлення, лічильники й результати, а `IProgress<DownloadProgress>` отримує початковий, проміжний і кінцевий стан. Некоректні рядки стають помилками без звернення до Drive; порожні рядки виключає ExcelReader. Скасований поточний і необроблені рядки стають пропущеними. Уже перейменований файл залишається успішним навіть за пізнього скасування.

`DownloadRetryPolicy` використовує налаштовані 0–3 повтори: 1/2/4 с плюс випадковий jitter до 1 с, з очікуванням щонайменше Retry-After. Бюджет спільний для метаданих і передачі одного рядка: максимум чотири проходи конвеєра. Повтор починається з нових метаданих і нового порожнього `.part`; прибирання попередньої спроби завершується до затримки. SDK backoff залишається вимкненим, окремий одноразовий OAuth refresh після 401 не множить transient retry. Відсутність доступу, 404, неправильні дані, непідтримуваний тип і ліміт експорту не повторюються. Втрата авторизації, переповнення диска, недоступність сховища й непередбачена помилка зупиняють пакет. Сирі винятки й resource keys не передаються до результатів або журналу.

**Критерії готовності:** виконано автоматичними тестами. Перевірено послідовність передач і відхилення другого запуску, повторний запуск після завершення/помилки, бюджети 0/1/2/3, спільний бюджет метаданих/передачі, Retry-After і jitter, новий порожній `.part`, відсутність подвійного підрахунку, продовження після помилок рядків, критичні зупинки, скасування під час метаданих/запису/затримки та після commit, попереднє скасування, порожній пакет, помилки книги, безпечні повідомлення й несправний спостерігач прогресу. Інтеграційний тест використовує справжні ExcelReader, Google SDK та FileStorageService із синтетичним XLSX і підміненим HTTP: rate-limit 403 → неповний вміст → успіх; 404 і неправильний URL не заважають подальшому DOCX-експорту. Наявний PDF не перезаписано, XLSX не змінено, `.part` не залишилися.

**Фактичні перевірки:** локальний SDK .NET 10.0.402; `dotnet restore GoogleDriveDownloader.sln --locked-mode` — успішно; `dotnet build GoogleDriveDownloader.sln --no-restore` — 0 помилок, 0 попереджень; `dotnet test GoogleDriveDownloader.sln --no-build --no-restore` — **418 пройдено, 0 помилок, 0 пропущено** (45 нових перевірок). Тести виконано без мережі й реальних credentials; повний набір запускався із завантаженим профілем Windows для DPAPI. Виправлено початкове використання синхронного xUnit Assert.Throws для Task у новому тесті; кінцева збірка успішна.

**Створені файли:**

- `src/GoogleDriveDownloader/Application/Batch/`: `BatchDownloadService.cs`, `DownloadRetryPolicy.cs`, `DownloadResult.cs`, `DownloadProgress.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `BatchDownloadServiceTests.cs`, `DownloadRetryPolicyTests.cs`, `BatchPipelineTests.cs`, `BatchTestDoubles.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Bootstrapper.cs`, `tests/GoogleDriveDownloader.Tests/BootstrapperTests.cs`, `README.md`, `TASKS.md`. SPEC.md, AGENTS.md, залежності та lock-файли не змінено.

**Обмеження:** повний інтерфейс пакетного завантаження належить до етапу 9 і ще не підключений. Якщо читання книги перервано до отримання списку рядків, підсумок має нуль відомих записів; кількість невідомих рядків не вигадується. Зберігаються обмеження ClosedXML щодо синхронного відкриття книги та файлового сервісу щодо шляхів/прибирання `.part`. Retry-After не обмежується штучною верхньою межею, але очікування можна скасувати. Реальні Google-запити, GUI та фізичні збої диска/SMB не перевірялися. Етапи 9–11 не розпочато.

### Етап 9. WinForms-інтерфейс

**Статус:** завершено 18.10.2026.

**Кроки:**

1. [x] Створити головне вікно приблизно 700 × 500 із контролами, передбаченими SPEC.
2. [x] Підключити вхід, вихід, імпорт credentials, вибір XLSX і директорії.
3. [x] Відображати прогрес за обробленими рядками, лічильники й кольоровий журнал із часом.
4. [x] Оновлювати контроли через створений у UI-потоці `IProgress<T>`.
5. [x] Блокувати повторний запуск і зміну параметрів під час роботи.
6. [x] Після нормального завершення показувати «Готово» зі статистикою, після скасування — «Скасовано».
7. [x] При закритті активного вікна скасовувати операцію й дочекатися очищення ресурсів.

**Результат:** головна форма 700 × 500 із гнучким табличним компонуванням, статусом авторизації, імпортом OAuth Desktop JSON, входом/виходом/зміною акаунта, полями шляхів і кнопками OpenFileDialog/FolderBrowserDialog, запуском та спільною кнопкою скасування OAuth/завантаження. `BatchDownloadService` отримується через DI. На час читання книги, перевірки шляхів, передачі та retry заблоковано запуск, зміну шляхів, імпорт і дії з акаунтом. Порожні credentials залишають програму працездатною з інструкцією в статусі та журналі; запуск потребує авторизації й непорожніх шляхів. Перевірки наявності файлів/директорії працюють у Task.Run; XLSX і конвеєр використовують наявні фонові сервіси.

Читання починається з невизначеного прогресу; після отримання рядків відображаються Processed/Total та Succeeded/Failed/Skipped. Підсумок встановлюється безпосередньо з результату пакета, а застарілі Progress-повідомлення ігноруються за поколінням запуску. Після нормального завершення показується «Готово» зі статистикою навіть із помилками рядків; після скасування — «Скасовано»; критична помилка має окреме повідомлення. Контроли повертаються до доступного стану; при втраті авторизації спочатку потрібен новий вхід.

RichTextBox показує вже очищені LoggingService події з локальним часом, номером рядка, назвою та HTTP-кодом: успіх — зелений, Error — червоний, інші події — звичайний колір. Переноси з вхідних назв/повідомлень замінюються пробілами, щоб одна подія займала один рядок. Файловий журнал і UI використовують той самий потік подій; збій файлового журналу дає попередження й не блокує пакет.

`Progress<LogEntry>`, `Progress<AuthenticationStatus>` та `Progress<DownloadProgress>` створюються у UI-потоці з WindowsFormsSynchronizationContext, відповідно до [контракту Progress](https://learn.microsoft.com/en-us/dotnet/api/system.progress-1?view=net-10.0). При закритті активного вікна FormClosing скасовує перше закриття, запитує скасування операції та асинхронно чекає `_pendingTask`, залишаючи message loop активним; потім вікно закривається повторно. Підписки знімаються під час Dispose, запізнілі сповіщення не звертаються до закритих контролів. Бізнес-сервіси й затверджена архітектура не змінені.

**Критерії готовності:** виконано автоматично на справжньому WinForms STA message loop із невидимими вікнами. 14 нових перевірок охоплюють повний XLSX → пакет → файли → UI, статистику та кольори журналу, номери рядків/HTTP/час, перевірку UI-потоку й увімкнений CheckForIllegalCrossThreadCalls, повторний запуск без перезапису, обробку помилкових рядків, скасування під час запису/retry, блокування параметрів і повторного запуску, доступність message loop, очікування очищення `.part` під час повторних спроб закриття, ігнорування пізніх логів, імпорт/вхід/зміну акаунта/вихід, скасування й закриття під час входу, неправильні шляхи та пошкоджену книгу з виправленням без перезапуску, втрату авторизації, скасування/помилку діалогу, маскування секретів і попередження файлового журналу. Підмінено тільки HTTP/receiver, Drive-контракт і модальні діалоги; ExcelReader, файловий сервіс, LoggingService, OAuth SDK та DPAPI справжні.

**Фактичні перевірки:** локальний SDK .NET 10.0.402; `dotnet restore GoogleDriveDownloader.sln --locked-mode` — успішно; `dotnet build GoogleDriveDownloader.sln --no-restore` — **0 помилок, 0 попереджень**; `dotnet test GoogleDriveDownloader.sln --no-build --no-restore` — **432 пройдено, 0 помилок, 0 пропущено** (418 попередніх і 14 нових). Тести виконані під завантаженим Windows-профілем для DPAPI; без справжніх credentials, мережі, браузера чи видимих вікон. Виправлено конфлікт простору імен Application у тестовому host, атрибути серіалізації тестової форми для WinForms-аналізатора й неправильний тип контрола в одному тесті. Кінцева збірка й усі тести успішні.

**Створені файли:**

- `src/GoogleDriveDownloader/Presentation/`: `MainForm.Batch.cs`, `MainForm.Dialogs.cs`.
- `tests/GoogleDriveDownloader.Tests/`: `MainFormTests.cs`, `MainFormTestContext.cs`, `WinFormsTestHost.cs`.

**Змінені файли:** `src/GoogleDriveDownloader/Presentation/MainForm.cs`, `src/GoogleDriveDownloader/Presentation/MainForm.Designer.cs`, `README.md`, `TASKS.md`. SPEC.md, AGENTS.md, залежності та lock-файли не змінено.

**Обмеження:** ручна перевірка системних діалогів, масштабування на різних DPI та реального Google-входу/завантаження не виконувалася без власних credentials. Автоматичні перевірки підміняють вибір у діалогах і мережу, не замінюючи ці ручні сценарії. Синхронне відкриття ClosedXML і окремі системні перевірки шляху не підтримують миттєвого скасування: UI залишається чутливим, закриття чекає завершення цих фонових викликів. Повне ручне приймання, розширена документація й Single File publish залишаються етапами 10–11; їх не розпочато.

### Етап 10. Повна перевірка й документація

**Статус:** завершено 18.10.2026 в межах локальних автоматичних перевірок і документації. Умовний крок із реальним Google не виконано без власних credentials користувача; усі ручні сценарії мають окремий відкритий статус.

**Кроки:**

1. [x] Закрити всі 15 мінімальних груп тестів із SPEC та перевірити взаємодію сервісів на підмінах.
2. [x] Підготувати синтетичні XLSX без приватних даних.
3. [x] Написати український README за всіма пунктами SPEC, включно з Google Cloud, consent screen, імпортом, очищенням токенів і типовими помилками.
4. [ ] Виконати ручну перевірку приватних файлів, трьох експортів, відсутнього доступу, скасування й повторного входу за наявності власних credentials користувача. **Не виконано:** credentials і дозволені приватні документи не надані; критерії внесено до docs/TESTING.md, вигадані ключі не використовувались для реального OAuth.
5. [x] Перевірити журнали й доступні матеріали постачання на відсутність секретів. Перевірено репозиторні файли, приклади, Debug EXE/DLL, зовнішню конфігурацію та тестові журнали. Фактичний dist ще відсутній; його окрема перевірка належить до етапу 11.

**Результат:** усі 15 мінімальних груп мають явну відповідність автоматичним тестам у docs/TESTING.md; там же кожному розділу SPEC зіставлено перевірку або окремий ручний/майбутній статус. Додано три постійні синтетичні XLSX: звичайний файл і три Workspace-типи з дублікатом, змішані валідні/невалідні записи з hyperlink і кешем формули, неправильні заголовки. Приватних даних немає; усі IDs/keys вигадані. Тестовий проєкт копіює їх до Samples; тести перевіряють фізичні рядки, кеш без обчислення, помилки й незмінність книги.

Нові інтеграційні тести використовують справжні OAuth SDK/DPAPI, Drive SDK, ExcelReader, BatchDownloadService, FileStorageService і LoggingService; підмінено OAuth receiver та HTTP. Перевірено всі три MIME експорту й розширення, resource key, дублікат без перезапису, статистику та відповідність подій очищеному JSONL; повторний 401 зупиняє пакет і очищає авторизацію, новий явний вхід дозволяє новий пакет; скасування фактичного SDK-потоку очищає `.part` і позначає решту рядків пропущеними. Синтетичні байти відповіді не підтверджують коректність Office-вмісту реального Google-експорту.

README доповнено системними вимогами, створенням Cloud проєкту, увімкненням Drive API, Branding/Audience/Data Access, test users, Desktop Client/JSON, обмеженнями Restricted Scope, строком refresh token у Testing, очищенням локальних токенів, перенесенням програми та таблицею типових помилок. Поточні налаштування Google звірено з офіційними джерелами, посилання наведено в README. Інструкція майбутнього publish описана, команда не виконувалася й скрипт етапу 11 не створювався.

**Критерії готовності:** доступну автоматичну частину виконано без мережі, реальних credentials і Office; матриця вимог та відкриті ручні сценарії з критеріями готовності задокументовані. Невиконані Google-перевірки та майбутнє приймання дистрибутива не позначено успішними.

**Фактичні перевірки:** SDK .NET 10.0.402, середовище DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME та NUGET_PACKAGES як на попередніх етапах. Нових залежностей і змін lock-файлів немає.

| Команда | Фактичний результат |
|---|---|
| `dotnet restore GoogleDriveDownloader.sln --locked-mode --force --verbosity minimal` | Успішно для обох проєктів. Початковий sandbox-запуск локального генератора прикладів отримав NU1900 через недоступні метадані NuGet; повторний restore із доступом до NuGet успішний, перевірка вразливостей не вимикалася |
| `dotnet build GoogleDriveDownloader.sln --no-restore --verbosity minimal` | Успішно, Debug; 0 попереджень, 0 помилок |
| Нові SampleWorkbookTests і AcceptancePipelineTests | 6 пройдено |
| `dotnet test GoogleDriveDownloader.sln --no-build --no-restore --verbosity minimal --logger 'trx;LogFileName=stage10.trx' --results-directory .tools/test-results` | **438 пройдено, 0 помилкових, 0 пропущених**; локальний TRX не входить до Git |
| Перевірка секретів і Git-виключень | Ознак реальних credentials/токенів у перевірених файлах не знайдено; ClientId/ClientSecret порожні в джерельному й вихідному appsettings; межі пошуку — docs/SECURITY-REVIEW.md |
| `git diff --check` | Успішно |

Повний набір запускався під завантаженим Windows-профілем для справжнього DPAPI; UI-тести мали невидимі STA-вікна. Браузер, loopback listener і реальні Google API під час тестів не використовувалися. Генератор XLSX був одноразовим локальним інструментом у виключеному `.tools`; програма й тести не потребують його або Python/Office.

**Створені файли:** `samples/downloads.xlsx`, `samples/validation.xlsx`, `samples/invalid-headers.xlsx`, `samples/README.md`, `docs/TESTING.md`, `docs/SECURITY-REVIEW.md`, `tests/GoogleDriveDownloader.Tests/SampleWorkbookTests.cs`, `tests/GoogleDriveDownloader.Tests/AcceptancePipelineTests.cs`.

**Змінені файли:** `tests/GoogleDriveDownloader.Tests/GoogleDriveDownloader.Tests.csproj`, `README.md`, `TASKS.md`. Production-код, SPEC.md, AGENTS.md, залежності та lock-файли не змінено.

**Обмеження:** ручні приватний файл, Docs/Sheets/Slides, відсутність доступу, реальний OAuth/refresh/revocation/повторний вхід, Shared Drives/resource keys, DPI та нативні діалоги залишаються відкритими. Перевірка секретів обмежена описаними форматами й доступними файлами, не є доказом відсутності довільних секретів. Self-Contained Single File, publish-скрипт, склад dist і запуск на чистій Windows не перевірялися; етап 11 не розпочато.

### Етап 11. Self-Contained Single File і приймання

**Кроки:**

1. Налаштувати Release publish із усіма параметрами SPEC: `win-x64`, SelfContained, SingleFile, self-extract native libraries, без trimming та AOT.
2. Залишити `appsettings.json` зовнішнім; використовувати лише порожні credentials у дистрибутиві.
3. Створити PowerShell-скрипт публікації в `dist/win-x64/` із перевіркою кодів завершення.
4. Включити EXE, конфігурацію та README; виключити токени, логи, завантаження й приватні файли.
5. Перевірити опублікований EXE на Windows без установленого .NET та Office, зокрема запуск із директорії без права запису.

**Результат:** автономний дистрибутив для передавання користувачу.

**Критерії готовності:** publish успішний; застосунок читає зовнішню конфігурацію, використовує LocalAppData й проходить користувацький сценарій із дистрибутива. Непроведені перевірки на чистій машині залишаються відкритими.

## 4. Контроль готовності й облік прогресу

Після кожного етапу реалізації:

1. Виконати `dotnet restore`, `dotnet build` та відповідні `dotnet test`.
2. Виправити спричинені етапом помилки.
3. Оновити в `TASKS.md` статус, створені й змінені файли, фактичні результати перевірок та обмеження.
4. Позначати етап завершеним лише після виконання його критеріїв; недоступні перевірки фіксувати окремо.
5. Зупинитися до окремого запиту на наступний етап.

Загальне приймання відповідає розділу 19 SPEC. Збірка, локальні тести, реальна Google-інтеграція та запуск автономного дистрибутива обліковуються окремо: успішність одного виду перевірки не замінює інші.

## 5. Додаткові зміни за окремими запитами

### Налаштовувані заголовки XLSX — 26.10.2026

**Статус:** завершено за прямим запитом користувача; етап 11 не розпочато.

У секції Excel зовнішнього appsettings.json додано два ключі: NameColumnHeader (типово «Назва») та LinkColumnHeader (типово «Посилання»). ConfigurationLoader завантажує значення, обрізає крайові пробіли, відхиляє порожні й однакові без урахування регістру назви та неправильну структуру. Якщо ключів немає, старі конфігурації працюють зі значеннями за замовчуванням.

ExcelReader отримує AppSettings через наявний DI-контейнер; налаштовані назви застосовуються до пошуку стовпців і повідомлень про відсутність/дублювання заголовків та помилки рядків. Читання без явно переданих налаштувань зберігає попередні defaults для існуючих викликачів. Порівняння залишається нечутливим до регістру, порядок стовпців довільний, обробка hyperlink і формул не змінена. README містить ключі, приклад власних заголовків та вимогу перезапуску.

**Перевірки:** restore за lock-файлами успішний; кінцевий build — 0 помилок, 0 попереджень; повний dotnet test — **455 пройдено, 0 помилкових, 0 пропущених** (17 нових перевірок). Перевірено власні заголовки українською/англійською, переставлені ролі стандартних назв, регістр/пробіли, неправильні налаштування, динамічні повідомлення й шлях JSON → ConfigurationLoader → DI → ExcelReader. Конфігураційний тест із порожніми credentials ізольовано в тимчасовому каталозі, щоб не читати особисту конфігурацію розробника. Повний набір працював під Windows-профілем для DPAPI та невидимих WinForms-вікон, без Google і реальних credentials.

Початкові NU1900 через SSL-доступ до метаданих NuGet усунено повторним restore поза sandbox без вимкнення аудиту; неправильне розташування using у новому тесті виправлено до кінцевої збірки. git diff --check виконано успішно. Наявні локальні значення інших секцій appsettings.json збережено; секрети до тестів або документації не копіювалися.

**Змінені файли:** src/GoogleDriveDownloader/Application/Configuration/AppSettings.cs, src/GoogleDriveDownloader/Infrastructure/Configuration/ConfigurationLoader.cs, src/GoogleDriveDownloader/Application/Input/ExcelReader.cs, src/GoogleDriveDownloader/appsettings.json; tests/GoogleDriveDownloader.Tests/ConfigurationLoaderTests.cs, ExcelReaderTests.cs, BootstrapperTests.cs; README.md, TASKS.md. Нових файлів і залежностей немає. SPEC.md та AGENTS.md не змінено.

**Обмеження:** зміни Excel-налаштувань застосовуються після перезапуску. Реальні Google-операції й майбутню публікацію це завдання не перевіряло.
