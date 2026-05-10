using StudyScheduler.Bot.Core.Conversations;

namespace StudyScheduler.Bot.Flows.AddStudent;

public sealed class AddStudentState : IFlowState
{
    public const string AskName = "ask_name";
    public const string AskPrice = "ask_price";
    
    public string FlowName => AddStudentFlow.Name;
    
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    
    public string CurrentStep { get; set; } = AskName;
    
    public string? Name { get; set; }
    
    public decimal? PricePerLesson { get; set; }
}