# Перевірки й ручне приймання

Стан етапу 10: автоматичні перевірки виконуються без Google credentials, браузера, зовнішнього HTTP й Office. Windows, .NET SDK 10 відповідно до global.json і завантажений профіль поточного користувача потрібні для справжнього DPAPI; WinForms-тести працюють у власному STA message loop із невидимими вікнами. Встановлення/відновлення NuGet-пакетів — окрема мережева операція до запуску тестів.

Команди з кореня репозиторію:

```powershell
dotnet restore GoogleDriveDownloader.sln --locked-mode
dotnet build GoogleDriveDownloader.sln --no-restore
dotnet test GoogleDriveDownloader.sln --no-build --no-restore
```

У поточному workspace використовується `.tools/dotnet/dotnet.exe`; DOTNET_ROOT і DOTNET_ROOT_X64 вказують на `.tools/dotnet`, DOTNET_CLI_HOME — `.tools/cli-home`, NUGET_PACKAGES — `.tools/nuget/packages`. Сам каталог `.tools` не постачається. DPAPI не підміняється; середовище без Windows-профілю не може підтвердити ці тести.

Фактичний запуск 18.10.2026 із SDK 10.0.402: locked restore і Debug build успішні, 0 попереджень/помилок; **438 тестів пройдено, 0 помилкових, 0 пропущених**, зокрема 6 нових перевірок етапу 10. Локальний VSTest-звіт збережено в `.tools/test-results/stage10.trx` та виключено з Git. Подробиці запуску й обмеження середовища наведено в TASKS.md.

## 15 мінімальних груп зі SPEC §14

Усі названі класи розташовані в `tests/GoogleDriveDownloader.Tests`.

| № | Вимога | Перевірка |
|---|---|---|
| 1 | Основні Google Drive URL | DriveLinkParserTests.ParsesRequiredAndSupportedAlternateLinks: усі шість форм SPEC |
| 2 | Resource key | DriveLinkParserTests.PreservesResourceKeySeparatelyFromFileId; GoogleDriveServiceTests: окремі заголовки metadata/media/export; AcceptancePipelineTests: Docs із ключем |
| 3 | Чужі домени | DriveLinkParserTests.RejectsMalformedUnsupportedUntrustedAndAmbiguousLinks; SampleWorkbookTests: example.invalid |
| 4 | Некоректні/порожні URL | DriveLinkParserTests; ExcelReaderTests.KeepsIndependentErrorsAndContinuesAfterInvalidRows |
| 5 | Два стовпці XLSX | ExcelReaderTests.ReadsHeadersInEitherOrderIgnoringCaseAndWhitespace; SampleWorkbookTests |
| 6 | Справжній hyperlink | ExcelReaderTests.HyperlinkTargetHasPriorityOverDisplayedText; SampleWorkbookTests: validation.xlsx |
| 7 | Порожні рядки/заголовки | ExcelReaderTests.PreservesPhysicalRowsAndIgnoresEmptyWhitespaceAndFormattingOnlyRows; MissingOrDuplicateRequiredHeadersAreWorkbookErrors; SampleWorkbookTests |
| 8 | Назви файлів | FileNameResolverTests.ProducesSafeNamesWithoutLosingMeaningfulDots; DeterminesExtensionFromActualMetadata |
| 9 | Дублікати | FileStorageServiceTests.ExistingFilesCaseVariantsAndDirectoriesReceiveSuffixesWithoutOverwrite; CollisionAppearingBetweenCheckAndMoveCannotOverwriteFileOrDirectory; AcceptancePipelineTests |
| 10 | Заборонені Windows-символи | FileNameResolverTests.ProducesSafeNamesWithoutLosingMeaningfulDots; UntrustedInputBecomesOneFileName |
| 11 | Traversal/зарезервовані імена | FileNameResolverTests.StorageBoundaryRejectsUnresolvedUnsafeNames; FileStorageServiceTests.ResolvedTraversalInputStaysWithinChosenDirectory |
| 12 | Формати експорту | GoogleDriveServiceTests і FileNameResolverTests.ExportFormatOverridesRemoteMetadata; AcceptancePipelineTests: DOCX/XLSX/PPTX через SDK і файловий сервіс |
| 13 | Помилка окремого запису | BatchDownloadServiceTests.InvalidAndInaccessibleRowsDoNotStopSubsequentRowsOrDoubleCount; BatchPipelineTests; MainFormTests |
| 14 | Статистика | BatchDownloadServiceTests; AcceptancePipelineTests: успіх, критична зупинка, скасування; MainFormTests: значення контролів |
| 15 | Скасування пакета | BatchDownloadServiceTests: metadata/write/retry/commit; AcceptancePipelineTests.CancellationOfActualSdkBodyRemovesPartAndSkipsEveryRemainingSampleRow; MainFormTests: скасування й закриття |

## Відповідність іншим розділам SPEC

| SPEC | Автоматична перевірка або окремий статус |
|---|---|
| §1–2: .NET 10/C# 14/WinForms, один production-проєкт, DI | Проєкти, Directory.Build.props, locked restore/build; BootstrapperTests; MainFormTests |
| §3: перший аркуш, hyperlink, формули без виконання, незмінність XLSX | ExcelReaderTests; три [збережені приклади](../samples/README.md) і SampleWorkbookTests |
| §4/4.1: PKCE, state, loopback, offline, refresh, Desktop JSON, DPAPI, вихід | GoogleAuthServiceTests, GoogleAuthLifecycleTests, OAuthConfigurationServiceTests, DpapiTokenStoreTests; реальний браузер/Google — ручна перевірка |
| §5–6: metadata/canDownload, Shared Drives, формати, ліміт, потокова передача | GoogleDriveServiceTests, GoogleDriveStreamingTests, StoredDriveDownloadTests; три експорти й справжній auth initializer — AcceptancePipelineTests |
| §7: імена, колізії, .part, cleanup | FileNameResolverTests, FileStorageServiceTests, BatchPipelineTests, AcceptancePipelineTests |
| §8: UI, кольори, прогрес, доступність message loop | MainFormTests із CheckForIllegalCrossThreadCalls; різні DPI/нативні діалоги — ручна перевірка |
| §9: JSONL, контекст, ротація, маскування, відмова sink | LoggingServiceTests; AcceptancePipelineTests звіряє події з файловим журналом у повному конвеєрі; [огляд секретів](SECURITY-REVIEW.md) |
| §10–11: помилки, retry/backoff/jitter/Retry-After, послідовність, статистика | Drive/Storage/Batch/Retry тести; MainFormTests; повторний 401 → критична зупинка → новий вхід і новий пакет — AcceptancePipelineTests |
| §12–13: зовнішня конфігурація, LocalAppData, порожні credentials | ConfigurationLoaderTests, OAuthConfigurationServiceTests, BootstrapperTests; UI без credentials — MainFormTests |
| §14: 15 груп | Матриця вище |
| §15: win-x64 Self-Contained Single File | Етап 11 не розпочато: publish, скрипт і чиста машина не перевірялися |
| §16: README | Описані всі 15 тем; налаштування Google Cloud звірено з офіційною документацією, посилання в README |
| §17: якість/безпека | Restore/build/test, перевірки скасування/ресурсів/витоків; SDK-сигнатури перевірено на попередніх етапах; архітектура й залежності збережені |
| §18–19: порядок роботи/готовність | TASKS.md розділяє автоматичні перевірки, ручну Google-інтеграцію і майбутню публікацію; загальне приймання ще не закрите |

## Ручні сценарії з власним Google-акаунтом

Стан кожного рядка нижче: **не виконано**. Реальні credentials і дозволені приватні документи користувачем не надані. Підміни HTTP не підтверджують поведінку реального Google. Не додавайте заповнений приватними URL XLSX, OAuth JSON, токени чи журнали до Git.

| Сценарій | Дії та критерій успіху |
|---|---|
| Перший вхід | Налаштувати власний Desktop Client за README, імпортувати JSON, увійти в системному браузері; статус «Авторизовано», auth.dat з'явився, пароль не вводиться в програмі |
| Приватний звичайний файл | З дозволеного акаунта завантажити власний PDF/ZIP; порівняти вміст або SHA-256 з оригіналом; ім'я відповідає XLSX |
| Google Docs | Експортувати власний документ; кінцевий .docx відкривається й містить очікуваний текст |
| Google Sheets | Експортувати власну таблицю; кінцевий .xlsx відкривається й містить очікувані аркуші/значення |
| Google Slides | Експортувати власну презентацію; кінцевий .pptx відкривається й містить очікувані слайди |
| Немає доступу/заборона власника | Додати дозволений для перевірки недоступний файл та файл із забороненим завантаженням; зрозуміла помилка рядка, наступний доступний запис збережено, обмеження не обходяться |
| Дублікати | Два однакові імені та вже наявний файл у директорії; отримати (2)/(3), початковий файл незмінний |
| Скасування/закриття | На великому дозволеному файлі скасувати передачу, потім окремо закрити вікно під час роботи; UI чутливий, підсумок «Скасовано», власного .part після успішного очищення немає |
| Restart/refresh | Перезапустити програму зі збереженим кешем; відновлення без браузера. Після природного закінчення access token передача оновлює його без ручного редагування auth.dat |
| Відкликання й повторний вхід | Відкликати дозвіл у Google-акаунті, дочекатися наступного запиту/refresh; пакет не має нескінченних повторів, пропонується новий вхід; після нього новий пакет працює |
| Вихід/інший акаунт | «Вийти» → перезапуск без відновлення сесії; «Змінити акаунт» → дані й дозволи саме нового акаунта |
| Shared Drive/resource key | За наявності дозволеного файла в Shared Drive/посилання з ключем перевірити metadata й передачу, відсутність ключа в логах |
| Ліміт експорту/мережа | На власному тестовому Workspace-файлі понад ліміт експорту отримати помилку без кінцевого часткового файла; окремо перервати мережу та перевірити обмежені повтори/скасування |
| Журнали | Локально перевірити UI і JSONL на наявність контексту й відсутність власних credentials/токенів/resource keys; не публікувати приватний журнал |
| UI/ОС | Нативні OpenFileDialog/FolderBrowserDialog, клавіатура, масштабування 100/150/200%, цільові Windows 10/11 x64; контроли доступні, текст читається |
| Дистрибутив | Етап 11: win-x64 publish, запуск без .NET/Office і з директорії EXE без права запису; LocalAppData та зовнішня конфігурація працюють |

Збої реального диска, SMB і живлення не відтворювалися. Після аварійного завершення .part може залишитися; відновлення пакетів не входить до MVP. Відкриття документів у сторонньому переглядачі потрібне лише для ручної оцінки експорту, сам застосунок Office не потребує.
