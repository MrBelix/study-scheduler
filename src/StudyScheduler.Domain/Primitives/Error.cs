namespace StudyScheduler.Domain.Primitives;

/// <summary>
/// A single validation failure of user input against a domain invariant. <see cref="Field"/>
/// carries the exact PascalCase key the API exposes in ValidationProblem payloads
/// ("DurationMinutes", "Price", "Topic", ...) so the endpoint mapping — and therefore the
/// frontend's field highlighting — needs no translation layer.
/// </summary>
public sealed record Error(string Code, string Message, string? Field = null);
