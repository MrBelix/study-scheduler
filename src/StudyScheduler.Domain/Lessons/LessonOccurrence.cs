namespace StudyScheduler.Domain.Lessons;

/// <summary>A single computed occurrence of a <see cref="LessonSeries"/> (value object).</summary>
public readonly record struct LessonOccurrence(
    DateOnly OccurrenceDate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);
