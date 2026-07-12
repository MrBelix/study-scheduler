namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Identity of one series occurrence: the series plus its canonical scheduled local date. Matches
/// a virtual slot to the physical <see cref="Lesson"/> that materializes it (the unique
/// <c>(SeriesId, OccurrenceDate)</c> key), so expansion knows which slots are already taken.
/// </summary>
public readonly record struct SeriesSlot(Guid SeriesId, DateOnly OccurrenceDate);
