namespace StudyScheduler.Bot.Core.Conversations;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}