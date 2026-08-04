using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>Request body for creating a student under the current tutor.</summary>
public sealed record CreateStudentRequest(
    string Name,
    decimal Rate);

/// <summary>
/// Partial update — only non-null fields are applied. <c>Rate</c> is nullable so "not provided"
/// is distinguishable from 0.
/// </summary>
public sealed record UpdateStudentRequest(
    string? Name,
    decimal? Rate,
    StudentStatus? Status);

/// <summary>
/// The student's next upcoming lesson, so the client does not derive it from the schedule.
/// <c>Subject</c> is the lesson's topic, or the title of the series that generated it.
/// </summary>
public sealed record NextLessonResponse(
    DateTimeOffset StartUtc,
    string? Subject)
{
    public static NextLessonResponse? From(UpcomingLesson? nextLesson) =>
        nextLesson is null ? null : new NextLessonResponse(nextLesson.StartUtc, nextLesson.Subject);
}

/// <summary>Student projection returned to the client. <c>NextLesson</c> is null when none is due.</summary>
public sealed record StudentResponse(
    Guid Id,
    string Name,
    decimal Rate,
    StudentStatus Status,
    DateTimeOffset CreatedAtUtc,
    NextLessonResponse? NextLesson)
{
    public static StudentResponse From(Student student, UpcomingLesson? nextLesson = null) => new(
        student.Id,
        student.Name,
        student.Rate,
        student.Status,
        student.CreatedAtUtc,
        NextLessonResponse.From(nextLesson));
}

/// <summary>
/// The next lesson as the details screen needs it: the list's <see cref="NextLessonResponse"/> plus
/// the duration and <c>LessonId</c> — the same id <c>GET /lessons</c> serves, so opening the lesson
/// is one route whether it came from a series or was placed by hand.
/// </summary>
public sealed record NextLessonDetailsResponse(
    DateTimeOffset StartUtc,
    int DurationMinutes,
    string? Subject,
    Guid LessonId)
{
    public static NextLessonDetailsResponse? From(UpcomingLesson? nextLesson) =>
        nextLesson is null
            ? null
            : new NextLessonDetailsResponse(
                nextLesson.StartUtc,
                nextLesson.DurationMinutes,
                nextLesson.Subject,
                nextLesson.LessonId);
}

/// <summary>
/// What the student owes right now — the details screen's debt banner, computed here so the client
/// only has to render it: the money summed over their unpaid completed lessons of all time and how
/// many those are. NULL when they owe nothing, so "no banner" is a single check.
/// The lessons behind it are served by <c>GET /students/{id}/debts</c>.
/// </summary>
public sealed record StudentDebtResponse(
    decimal Amount,
    int LessonsCount)
{
    public static StudentDebtResponse? From(StudentDebtSummary? debt) =>
        debt is null ? null : new StudentDebtResponse(debt.Amount, debt.LessonsCount);
}

/// <summary>
/// One unpaid lesson of the debts screen: the row's own id — the one
/// <c>POST /lessons/settle</c> takes — when it ran, how long it was, what it costs, and
/// <c>Subject</c> read exactly as everywhere else (its topic, or the title of the series that
/// generated it).
/// </summary>
public sealed record DebtLessonResponse(
    Guid Id,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    decimal Price,
    string? Subject)
{
    public static DebtLessonResponse From(UnpaidLesson lesson) => new(
        lesson.LessonId,
        lesson.StartUtc,
        lesson.DurationMinutes,
        lesson.Price,
        lesson.Subject);
}

/// <summary>
/// Everything the debts screen shows: the unpaid lessons newest first, plus the same two numbers the
/// banner on the details screen carries — so the two screens cannot quote different sums. A student
/// who owes nothing answers with an empty list and zeros rather than a 404: they exist, they simply
/// owe nothing.
/// </summary>
public sealed record StudentDebtsResponse(
    IReadOnlyList<DebtLessonResponse> Lessons,
    decimal TotalAmount,
    int Count)
{
    public static StudentDebtsResponse From(IReadOnlyList<UnpaidLesson> unpaid) => new(
        unpaid.Select(DebtLessonResponse.From).ToList(),
        unpaid.Sum(l => l.Price),
        unpaid.Count);
}

/// <summary>
/// Everything the student details screen shows, in one response: the student itself, its next
/// lesson, the series it is still enrolled in, its lifetime totals and what it currently owes. The
/// lists keep the slim <see cref="StudentResponse"/> — this shape is only ever served for a single
/// student.
/// </summary>
public sealed record StudentDetailsResponse(
    Guid Id,
    string Name,
    decimal Rate,
    StudentStatus Status,
    DateTimeOffset CreatedAtUtc,
    NextLessonDetailsResponse? NextLesson,
    IReadOnlyList<LessonSeriesResponse> Series,
    int LessonsCompleted,
    decimal MoneyReceived,
    DateTimeOffset? FirstLessonAtUtc,
    StudentDebtResponse? Debt)
{
    public static StudentDetailsResponse From(
        Student student,
        UpcomingLesson? nextLesson,
        IReadOnlyList<LessonSeriesResponse> series,
        StudentLessonStats stats,
        StudentDebtSummary? debt) => new(
        student.Id,
        student.Name,
        student.Rate,
        student.Status,
        student.CreatedAtUtc,
        NextLessonDetailsResponse.From(nextLesson),
        series,
        stats.CompletedCount,
        stats.MoneyReceived,
        stats.FirstLessonAtUtc,
        StudentDebtResponse.From(debt));
}
