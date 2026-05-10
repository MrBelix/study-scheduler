using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Routing;

public readonly record struct CommandContext
{
    public Message Message { get; }
    public string Command { get; }
    public string[] Args { get; }

    public CommandContext(Message message)
    {
        Message = message;
        var parts = message.Text!.Split(' ');
        Command = parts[0];
        Args = parts.Length > 1 ? parts[1..] : [];
    }

    public long ChatId => Message.Chat.Id;
    public long UserId => Message.From!.Id;
}