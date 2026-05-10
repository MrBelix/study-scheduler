namespace StudyScheduler.Bot.Core.Conversations;

public interface IFlowState
{
    string FlowName { get; }
    
    DateTime CreatedAt { get; }
    
    string CurrentStep { get; set; }
}