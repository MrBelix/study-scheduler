namespace StudyScheduler.Bot.Core.Routing;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CallbackAttribute(string template) : Attribute
{
    public string Template { get; } = template;
}