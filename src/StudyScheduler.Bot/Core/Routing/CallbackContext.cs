using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Routing;

public readonly record struct CallbackContext
{
    public CallbackQuery Query { get; }
    public CallbackData Data { get; }

    public CallbackContext(CallbackQuery query, CallbackData data)
    {
        Query = query;
        Data = data;
    }

    // Шорткати до найчастіше потрібних полів
    public long ChatId => Query.Message!.Chat.Id;
    public int MessageId => Query.Message!.MessageId;
    public long UserId => Query.From.Id;
    public string CallbackQueryId => Query.Id;

    // Шорткат до параметрів
    public T Get<T>(string name) where T : IParsable<T> => Data.Get<T>(name);
    public string GetString(string name) => Data.GetString(name);
}