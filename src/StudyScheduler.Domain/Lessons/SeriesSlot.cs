namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Identity of one series occurrence: the series plus its canonical scheduled local date. Matches
/// one slot of a series to the <see cref="Lesson"/> that fills it (the unique
/// <c>(SeriesId, OccurrenceDate)</c> key), so expansion knows which slots are already taken.
/// </summary>
public readonly record struct SeriesSlot(Guid SeriesId, DateOnly OccurrenceDate);
