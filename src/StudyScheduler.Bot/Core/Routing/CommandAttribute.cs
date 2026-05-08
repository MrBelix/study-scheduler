namespace StudyScheduler.Bot.Core.Routing;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute(string command) : Attribute
{
    public string Command { get; } = command;
}
