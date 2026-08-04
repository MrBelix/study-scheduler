namespace StudyScheduler.API.Core.Tenancy;

/// <summary>
/// The scoped tenant holder: one instance per HTTP request or background scope, read through
/// <see cref="ITutorContext"/> and written through <see cref="ITutorScope"/>.
/// </summary>
public sealed class TutorContext : ITutorContext, ITutorScope
{
    private bool _fromAuthentication;

    public long? CurrentTutorTelegramId { get; private set; }

    public void SetFromAuthentication(long tutorTelegramId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);

        // One scope, one authenticated tutor. Re-entering the middleware with the SAME identity is
        // harmless and stays a no-op (a re-executed pipeline does exactly that), but a second,
        // different identity would move every filter and stamp under the work already done — a
        // defect, not a case.
        if (CurrentTutorTelegramId is { } current && current != tutorTelegramId)
            throw new InvalidOperationException(
                $"The tutor of this scope is already {current} and cannot be reassigned to {tutorTelegramId}.");

        CurrentTutorTelegramId = tutorTelegramId;
        _fromAuthentication = true;
    }

    public void SetForBackground(long tutorTelegramId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);

        // The tenant of an authenticated request is decided by its init data, upstream of any
        // payload. Nothing downstream may move it — a request that tries is a defect, not a case.
        if (_fromAuthentication)
            throw new InvalidOperationException(
                "The tutor of an authenticated request is fixed by its init data and cannot be reassigned.");

        CurrentTutorTelegramId = tutorTelegramId;
    }
}
