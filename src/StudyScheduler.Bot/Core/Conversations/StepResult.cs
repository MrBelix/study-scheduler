namespace StudyScheduler.Bot.Core.Conversations;

public abstract record StepResult
{
    public sealed record Next(string NextStep) : StepResult;          // йдемо далі
    public sealed record Repeat(string Reason) : StepResult;          // помилка валідації, повтор
    public sealed record Complete : StepResult;                        // флоу завершено
    public sealed record Cancel : StepResult;                          // скасовано
    public sealed record GoTo(string Step) : StepResult;              // перехід на конкретний крок
}