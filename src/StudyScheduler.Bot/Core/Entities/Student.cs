namespace StudyScheduler.Bot.Core.Entities;

public class Student : BaseEntity
{
    public string Name { get; private set; }
    
    public bool IsActive { get; private set; }
    
    public DateTimeOffset CreatedAt { get; private set; }
    
    private Student(int id, string name, bool isActive, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        IsActive = isActive;
        CreatedAt = createdAt;
    }

    public static Student Create(string name)
    {
        return new Student(0,
            name,
            true,
            DateTimeOffset.UtcNow);
    }
}