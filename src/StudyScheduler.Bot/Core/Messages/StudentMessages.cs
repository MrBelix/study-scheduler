using StudyScheduler.Bot.Core.Entities;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Core.Messages;

public static class StudentMessages
{
    public static BotMessage List(IReadOnlyList<Student> students) =>
        students.Count == 0 ? EmptyList() : FilledList(students);

    public static BotMessage Added(Student student, decimal? pricePerLesson) => new(
        $"✅ {student.Name} · {pricePerLesson} грн/урок",
        new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("👤 Картка", $"students:view:{student.Id}")],
            [InlineKeyboardButton.WithCallbackData("⬅️ До списку", "students:list")]
        ]));

    public static BotMessage AskName() => new("👤 Введіть ім'я учня:");

    private static BotMessage EmptyList() => new(
        "👨‍🎓 *Ваші учні*\n\nПоки що нікого немає.",
        new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("➕ Додати учня", "students:add")],
            [InlineKeyboardButton.WithCallbackData("⬅️ Меню", "menu:main")]
        ]),
        ParseMode.Markdown);

    private static BotMessage FilledList(IReadOnlyList<Student> students)
    {
        var rows = students
            .Select(s => new[] { InlineKeyboardButton.WithCallbackData(s.Name, $"students:view:{s.Id}") })
            .Append([InlineKeyboardButton.WithCallbackData("➕ Додати учня", "students:add")])
            .Append([InlineKeyboardButton.WithCallbackData("⬅️ Меню", "menu:main")])
            .ToArray();

        return new($"👨‍🎓 *Ваші учні* ({students.Count})", new InlineKeyboardMarkup(rows), ParseMode.Markdown);
    }
}
