namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Lifetime totals over one student's <see cref="Lesson"/> rows, aggregated by the database.
/// The <c>default</c> value is exactly the "this student has no lessons yet" answer.
/// </summary>
/// <param name="CompletedCount">Lessons whose status is <see cref="LessonStatus.Completed"/>.</param>
/// <param name="MoneyReceived">
/// Sum of the prices actually collected — paid, non-cancelled lessons. Same semantics as the
/// reports' actual income, so both screens show the tutor the same number.
/// </param>
/// <param name="FirstLessonAtUtc">Start of the earliest lesson of any status, or null if there is none.</param>
public readonly record struct StudentLessonStats(
    int CompletedCount,
    decimal MoneyReceived,
    DateTimeOffset? FirstLessonAtUtc);
