namespace StudyScheduler.API.Core.Tenancy;

/// <summary>
/// The write side of tenancy: the two — and only two — ways a scope gets its tutor.
/// <see cref="SetFromAuthentication"/> is for an HTTP request, whose tutor is whatever the validated
/// Telegram init data says and nothing else. <see cref="SetForBackground"/> is for work that has no
/// authenticated caller of its own: the nightly generator and the notification poller walk tenants
/// one at a time, and the Telegram webhook derives its tutor from the update payload.
/// Authentication always wins — see <see cref="SetForBackground"/>.
/// </summary>
public interface ITutorScope
{
    /// <summary>
    /// Establishes the tutor of an authenticated request from its principal. Called once per request
    /// by the tenancy middleware; the value is fixed from then on. Calling it again with the SAME
    /// tutor is a no-op (a re-executed pipeline is safe), with a DIFFERENT one it throws: a scope's
    /// identity cannot move once work has been done under it.
    /// </summary>
    void SetFromAuthentication(long tutorTelegramId);

    /// <summary>
    /// Establishes (or re-establishes) the tutor of a scope that has no HTTP identity, so the
    /// per-tenant work that follows reads and writes as that tutor. Throws when the scope already
    /// belongs to an authenticated request: an incoming HTTP call can never talk its way into
    /// another tenant, whatever it carries in its body.
    /// </summary>
    void SetForBackground(long tutorTelegramId);
}
