using StudyScheduler.API.Core.Tenancy;

namespace StudyScheduler.Tests.Core.Tenancy;

/// <summary>
/// A real <see cref="TutorContext"/> that also remembers, in order, every tenant it was put into —
/// so a background pass can be asserted to walk tenants one at a time instead of leaking across them.
/// </summary>
internal sealed class RecordingTutorScope : ITutorScope, ITutorContext
{
    private readonly TutorContext _inner = new();

    /// <summary>The tutors this scope was set to, in the order it was set to them.</summary>
    public List<long> Tenants { get; } = [];

    public long? CurrentTutorTelegramId => _inner.CurrentTutorTelegramId;

    public void SetFromAuthentication(long tutorTelegramId)
    {
        _inner.SetFromAuthentication(tutorTelegramId);
        Tenants.Add(tutorTelegramId);
    }

    public void SetForBackground(long tutorTelegramId)
    {
        _inner.SetForBackground(tutorTelegramId);
        Tenants.Add(tutorTelegramId);
    }
}
