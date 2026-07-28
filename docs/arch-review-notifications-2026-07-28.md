# Архітектурне рев'ю фічі Notifications (бот) — 2026-07-28

Зведення трьох незалежних оглядів (SOLID/архітектура, DRY/KISS, .NET best practices).
Статус: **беклог, не реалізовано**. Пункти беклогу з рев'ю 2026-07-13 (ack-first,
allowedUpdates, constant-time secret) підтверджені як досі відкриті — включені нижче.

## 🔴 Високий пріоритет — реальні продакшн-ризики

1. **Resilience handler дублює повідомлення.** Aspire ServiceDefaults вішає
   `AddStandardResilienceHandler()` на всі HttpClient-и, включно з клієнтом Telegram —
   неідемпотентний POST `sendMessage` прозоро ретраїться на 5xx/408/таймаут, тож "повільний
   успіх" на боці Telegram = тьютор отримує повідомлення двічі. Ретрай відбувається *під*
   класифікацією помилок у `TelegramBotSender`, раннер його не бачить.
   → Виключити цей клієнт з дефолтних resilience-політик.
   (`host/StudyScheduler.ServiceDefaults/Extensions.cs:29-36`, `NotificationsModule.cs:24`)

2. **Вебхук: обробка інлайн без ack-first і без дедуплікації `update_id`.** Мутація БД
   виконується до відповіді 200 і без try/catch — будь-який збій дає 500, Telegram
   редоставляє апдейт, а повторна обробка знову аплаїть патч і додає маркер до тексту
   повідомлення вдруге (`Endpoints.cs:46`, `TelegramWebhookHandler.cs:100`).
   → Ack одразу після перевірки секрета, обробка через `Channel<Update>` у фоновому
   консюмері + дедуп `update_id`. Сюди ж: у мутації тече `RequestAborted`-токен — обрив
   з'єднання Telegram може скасувати мутацію на пів дорозі; фонова обробка має жити на
   host-токені.

3. **Гонка матеріалізації заняття написана тричі з трьома різними політиками відновлення:**
   `NotificationRunner.cs:104-141` (прийняти чужий рядок), `LessonPatchService.cs:95-114`
   (повернути 409), `Features/Lessons/Endpoints.cs` (пре-чек).
   → Один `MaterializeAndPersistAsync` у `Core/Scheduling` з результатом "created or adopted".

4. **403-шлях затирає конкурентні зміни профілю.** `GetNotifiableAsync` віддає
   `AsNoTracking()`-снепшоти, а при 403 раннер робить `profiles.Update(profile)` на
   детачнутому об'єкті — full-row UPDATE зі стейл-даними: конкурентна зміна
   `RemindMinutes`/`TimeZone` через API мовчки губиться (`NotificationRunner.cs:194-196`).
   → `ExecuteUpdateAsync` тільки на `BotReachable`; варто додати `xmin` concurrency token
   на `Lesson`/`TutorProfile`.

5. **`NotificationRunner.SendOneAsync` — 135 рядків, 5 робіт, 13 залежностей у
   конструкторі.** Матеріалізація + гонка + таймзона/текст + відправка + персист у трьох
   `SaveChangesAsync`; бул-результат означає "тьютор ще досяжний".
   → Розбити на "resolve lesson" / "compose message" / "settle outcome" — конструктор
   схудне до ~4 залежностей.

## 🟡 Середній пріоритет — архітектура і стійкість

6. **SDK Telegram протікає крізь адаптерну межу.** Вебхук-хендлер типізований прямо на
   `Telegram.Bot.Types.Update` (єдина бізнес-логіка на вендорних типах — тому для нього
   немає юніт-тестів), а `INotificationSender` — "generic" інтерфейс з Telegram-only
   методами (`AnswerCallbackAsync`, `EditMessageAsync`), який email-канал реалізувати не
   зможе. → Транслювати `Update` у власний `BotInteraction` на рівні ендпоінта; розділити
   на канал-агностичний `INotificationSender` і `ITelegramInteraction`.

7. **Wire-формат callback-кнопок (`x:{guid}`) живе у 6 місцях**: закодований у
   `NotificationText` по одному разу на мову і розпарсений `switch`-ем у хендлері, плюс
   `TryParse` з `null!`-сентинелом. → Один `CallbackPayload` тип з `Encode`/`TryParse`.

8. **Реєстрація вебхука:** не передається `allowedUpdates` (Telegram шле всі типи
   апдейтів, хендлер використовує лише `callback_query` + `message`), а перманентна помилка
   конфігурації нескінченно крутиться на `LogWarning`, поки застосунок звітує "healthy"
   (`TelegramWebhookRegistrar.cs:33-53`).
   → `allowedUpdates: [CallbackQuery, Message]` + `dropPendingUpdates: true`; після N
   невдач — health check → Unhealthy.

9. **Порівняння секрета вебхука не constant-time** — звичайний `!=`, хоча auth-код уже
   робить правильно (`CryptographicOperations.FixedTimeEquals` у
   `TelegramInitDataValidator.cs:66`). → Той самий підхід для `Endpoints.cs:28`.

10. **429 ігнорує `RetryAfter`**: ретрай на наступному тику (за 1 хв) незалежно від
    прохання сервера; fan-out по тьюторах не тротлиться проти ліміту ~30 msg/s. Плюс
    `TaskCanceledException` від таймаута HttpClient не ловиться і абортить чергу тьютора
    (`TelegramBotSender.cs:30-37`).

11. **EF-гігієна раннера:** один DbContext на весь тик — `DiscardChanges()`
    (=`ChangeTracker.Clear()`) у циклі стирає трекінг непов'язаних сутностей (безпечно
    лише завдяки крихкому інваріанту "кожна ітерація сейвить"); N+1 по series/lesson на
    кожну нотифікацію; всі тьютори послідовно без пейджингу.
    → Скоуп на тьютора, преліт series батчем.

12. **Нотифікації лізуть у нутрощі фічі Lessons** — єдиний cross-feature `using` у всьому
    API: хендлер викликає `LessonPatchService` з HTTP-шейпнутим `UpdateLessonRequest`
    (`TelegramWebhookHandler.cs:86-87`).
    → Підняти патч-пайплайн у `Core/` або вузький порт `ILessonMutations`.

13. **`BotReachable` — стейт-машина з двома власниками**: poller вимикає
    (`NotificationRunner.cs:194-196`), webhook вмикає (`TelegramWebhookHandler.cs:41-51`),
    кожен комітить сам. Плюс профіль читається до 3 разів на один callback.
    → Маленький `BotReachability`-сервіс + один fetch профілю на запит.

14. **Часова математика вікон дубльована** між планером і раннером (запитне вікно vs
    due-предикати), а інваріант "тик ≤ мінімальний lead" тримається лише на валідаторі
    опцій у третьому файлі. Валідатор не перевіряє формат секрета (1–256 символів,
    `A-Za-z0-9_-`) та HTTPS/порт для `WebhookUrl` — така помилка виявиться лише
    нескінченним ретраєм реєстратора. `FollowUpLookbackMinutes` без верхньої межі.

## 🟢 Низький пріоритет / косметика

15. Вебхук-роут видно в OpenAPI (немає `ExcludeFromDescription`) і без ліміту розміру
    тіла — суперечить дизайну "404 щоб не світити ендпоінт" (`NotificationsModule.cs:47`).
16. `ITelegramBotClient`-синглтон тримає один `HttpClient` назавжди — DNS-refresh, який
    обіцяє коментар, фактично не працює; типізована реєстрація
    (`AddHttpClient(...).AddTypedClient<ITelegramBotClient>`) це виправить.
17. Два стейл-коментарі від переписування 12.07: у `TelegramBotSender:12-13` — "not
    registered in DI here", хоча зареєстрований у `NotificationsModule.cs:32`; у
    `TelegramWebhookHandler.cs:37` — неточний опис non-callback апдейтів.
18. `LessonPatchOutcome` — закрита ієрархія для вичерпного matching-у, але хендлер
    розрізняє лише `Ok` і логує тип через рефлексію (`TelegramWebhookHandler.cs:105-111`).
19. Дрібне: `NotificationText` очікує вже локалізований час (сигнатура не форсить —
    UTC-значення скомпілюється і відрендерить хибний час); 6 однакових best-effort
    catch-блоків у сендері (`TelegramBotSender.cs:57-103`); options змішують
    cadence-політику з секретами транспорту; `NotificationRunner.cs:71-74` повторно
    запитує студентів, яких `ScheduleReader` щойно завантажив.

## 📖 Як працює фоновий сервіс (`NotificationPollerService`)

`NotificationPollerService` — це `BackgroundService` (hosted service): ASP.NET Core запускає
його `ExecuteAsync` один раз при старті застосунку, і той живе поруч із веб-сервером до
самого shutdown. Життєвий цикл одного запуску:

1. **Старт.** Хост викликає `ExecuteAsync(stoppingToken)`. Сервіс читає
   `PollIntervalMinutes` з опцій (дефолт 1 хв) і створює `PeriodicTimer` з цим інтервалом.
2. **Цикл тиків.** `while (await timer.WaitForNextTickAsync(...))` — прокидання раз на
   інтервал. На кожному тику:
   - відкривається **свіжий DI-скоуп** (`CreateAsyncScope`) — бо сам сервіс синглтон, а
     `NotificationRunner`, репозиторії та `AppDbContext` — scoped; без скоупа один
     DbContext жив би вічно і накопичував трекінг;
   - з нього резолвиться `NotificationRunner` і виконується один прохід: знайти
     нотифікації, що "дозріли" (нагадування перед заняттям, follow-up після), і
     відправити їх;
   - скоуп диспозиться — все, що тик нажив, звільняється.
3. **Ізоляція збоїв.** Тик обгорнутий у `try/catch (Exception)`: якщо впала БД чи
   Telegram — помилка логується, цикл живе, наступний тик пробує знову. Це критично: у
   .NET 8+ необроблений виняток із `BackgroundService` **зупиняє весь хост** (тобто без
   цього catch один збій БД поклав би все API).
4. **Graceful shutdown.** При зупинці застосунку хост скасовує `stoppingToken`;
   `WaitForNextTickAsync` кидає `OperationCanceledException`, який хелпер трактує як
   "тиків більше не буде" (`return false`) — цикл завершується штатно, без стектрейсів
   у логах. Скасування *посеред* тика теж розпізнається (`catch OCE when
   stoppingToken.IsCancellationRequested`) і завершує цикл, а не логується як помилка.

Метод `RunTickAsync` навмисно винесений окремо і зроблений `internal` — тести ганяють
тіло тика напряму, без таймера і без очікування хвилини (`InternalsVisibleTo` для
`StudyScheduler.Tests`).

### Навіщо саме `PeriodicTimer`

Це сучасний (з .NET 6) примітив саме для таких циклів, і він дає три речі, яких немає у
наївного `while { work; await Task.Delay(interval); }`:

- **Захист від накладання тиків (overlap).** `WaitForNextTickAsync` не викликається,
  поки `await RunTickAsync` не завершився — два тики фізично не можуть виконуватись
  одночасно. Якщо тик тривав довше за інтервал, пропущені прокидання **не буферизуються**:
  таймер віддасть один наступний тик, а не чергу з N штук. З `Task.Delay` довгий тик
  зсуває весь розклад (інтервал = тривалість тика + delay), а з `System.Timers.Timer`
  callback-и накладалися б і два тики слали б ті самі нагадування двічі.
- **Стабільна кадентність.** Таймер цілиться в рівний період, а не в "тривалість тика +
  інтервал" — важливо, бо валідатор опцій гарантує `PollIntervalMinutes ≤ MinRemindMinutes`:
  саме рівний крок не дає нагадуванню "провалитись" між двома тиками.
- **Чисте скасування.** `WaitForNextTickAsync(ct)` прокидається миттєво при shutdown —
  не треба чекати кінця хвилинної паузи, щоб зупинити застосунок (у `Task.Delay(interval)`
  без токена це була б пауза до кінця інтервалу, з токеном — той самий OCE, але без
  зручної семантики "false = кінець циклу").

Разом із вебхуком це дає дворівневу доставку: poller — гарантований канал (працює навіть
без публічного URL, "poller-only mode"), вебхук — миттєва реакція на натискання кнопок.

## ✅ Що зроблено добре (не чіпати)

- `NotificationPlanner` — чистий, без I/O, без вендорних типів, ідеальний розмір.
- `TelegramBotSender` — справжній адаптер із продуманою класифікацією 403/400/429;
  відправка без `parseMode` = захист від Markdown/HTML-ін'єкції через імена студентів
  (варто задокументувати як load-bearing).
- `NotificationPollerService` — підручникова гігієна `BackgroundService`: скоуп на тик,
  ізоляція винятків (у .NET 8+ необроблений виняток зупинив би хост), `PeriodicTimer`
  дає природний захист від overlap, тестовий шов `RunTickAsync`.
- Persist-before-send + catch 23505 (`SqlErrors.IsDuplicateKey`) на реальному partial
  unique index — правильна at-least-once семантика без retry-циклів.
- `Endpoints.cs`: 404 (не 401) на поганий секрет — не світить існування ендпоінта;
  200 на сміття — без retry-штормів; ручний `JsonSerializer` з `JsonBotAPI.Options`
  замість `[FromBody]` — коректно обходить глобальний camelCase-конфіг.
- `MarkBotUnreachable/MarkBotReachable` — іменовані доменні переходи замість публічного bool.
- Реєстратор вебхука не валить хост при транзієнтному збої Telegram на старті.
- `NotificationsOptionsValidator` пов'язує `PollIntervalMinutes` з доменним інваріантом
  `TutorProfile.MinRemindMinutes` і падає на буті, а не мовчки пропускає нагадування.
