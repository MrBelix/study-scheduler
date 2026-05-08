namespace StudyScheduler.Bot.Core.Routing;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CallbackAttribute(string callback) : Attribute
{
    public string Callback { get; } = callback;
}
