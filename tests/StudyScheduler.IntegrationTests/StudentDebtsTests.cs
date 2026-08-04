using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end coverage of the student-debt contract over the real stack (PostgreSQL container +
/// API): the banner on the student details screen, the list of unpaid lessons behind it and the bulk
/// settle that clears them. What only a real database can prove is that one definition of "money
/// owed" reaches both screens — the debt a student's page shows is the very debt the Money screen
/// bills them for — and that a settle moves exactly the rows it named.
/// Each test uses distinct tutor ids so the shared database stays isolated between tests.
/// </summary>
[Collection(nameof(AppCollection))]
public class StudentDebtsTests(AppFixture app)
{
    [Fact]
    public async Task Debt_on_the_details_screen_matches_the_dashboard_debtors()
    {
        var tutor = TelegramInitData.ForUser(7001, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);

        // Two lessons taught and never paid for…
        await CompleteLesson(tutor, studentId, PastDate(days: 120), paid: false);
        await CompleteLesson(tutor, studentId, PastDate(days: 60), paid: false);
        // …one settled, and one still to come: neither is owed.
        await CompleteLesson(tutor, studentId, PastDate(days: 30), paid: true);
        await CreateLesson(tutor, studentId, FutureDate(days: 3));

        var details = await GetDetails(tutor, studentId);
        var dashboard = await GetDashboard(tutor);

        // The same rows, the same money: the banner and the Money screen cannot disagree.
        Assert.NotNull(details.Debt);
        Assert.Equal(500m, details.Debt!.Amount);
        Assert.Equal(2, details.Debt.LessonsCount);

        var debtor = Assert.Single(dashboard.Debt.Debtors);
        Assert.Equal(studentId, debtor.StudentId);
        Assert.Equal(details.Debt.Amount, debtor.Amount);
        Assert.Equal(details.Debt.LessonsCount, debtor.LessonsCount);
        Assert.Equal(details.Debt.Amount, dashboard.Debt.Total);
    }

    [Fact]
    public async Task Debts_lists_the_unpaid_lessons_newest_first_with_their_subject()
    {
        var tutor = TelegramInitData.ForUser(7002, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);

        // A one-off in the past, named by hand…
        await CompleteLesson(tutor, studentId, PastDate(days: 10), paid: false, topic: "Fractions");
        // …and an occurrence of a weekly series, which carries no topic of its own.
        var monday = NextMonday();
        await CreateWeeklySeries(tutor, studentId, monday, title: "Algebra");
        var occurrenceId = await LessonIdOn(tutor, monday);
        await SetStatus(tutor, occurrenceId, "Completed");

        var debts = await GetDebts(tutor, studentId);

        // Newest first — the series occurrence is ahead of the past one-off — and each row reads
        // under its own topic, or under the schedule's name when nobody wrote one.
        Assert.Equal(2, debts.Count);
        Assert.Equal(500m, debts.TotalAmount);
        Assert.Equal(occurrenceId, debts.Lessons[0].Id);
        Assert.Equal("Algebra", debts.Lessons[0].Subject);
        Assert.Equal(250m, debts.Lessons[0].Price);
        Assert.Equal(60, debts.Lessons[0].DurationMinutes);
        Assert.Equal("Fractions", debts.Lessons[1].Subject);
        Assert.True(debts.Lessons[0].StartUtc > debts.Lessons[1].StartUtc);
    }

    [Fact]
    public async Task Settling_a_subset_shrinks_the_debt_and_settling_the_rest_clears_it()
    {
        var tutor = TelegramInitData.ForUser(7003, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);

        var first = await CompleteLesson(tutor, studentId, PastDate(days: 30), paid: false);
        var second = await CompleteLesson(tutor, studentId, PastDate(days: 20), paid: false);
        var third = await CompleteLesson(tutor, studentId, PastDate(days: 10), paid: false);

        // Act — the tutor was handed the money for one of the three.
        Assert.Equal(1, await Settle(tutor, [first]));

        var afterFirst = await GetDetails(tutor, studentId);
        Assert.Equal(500m, afterFirst.Debt!.Amount);
        Assert.Equal(2, afterFirst.Debt.LessonsCount);
        var stillOwed = await GetDebts(tutor, studentId);
        Assert.Equal([third, second], stillOwed.Lessons.Select(l => l.Id));

        // Act — and then for the rest.
        Assert.Equal(2, await Settle(tutor, [second, third]));

        // Assert — nothing owed is no banner at all, and an empty ledger behind it.
        var settled = await GetDetails(tutor, studentId);
        Assert.Null(settled.Debt);
        var empty = await GetDebts(tutor, studentId);
        Assert.Empty(empty.Lessons);
        Assert.Equal(0m, empty.TotalAmount);
        Assert.Equal(0, empty.Count);

        // Act — the same request again, as a flaky connection would retry it.
        Assert.Equal(2, await Settle(tutor, [second, third]));
        Assert.Null((await GetDetails(tutor, studentId)).Debt);
    }

    [Fact]
    public async Task Settling_a_lesson_that_was_not_taught_refuses_the_whole_batch()
    {
        var tutor = TelegramInitData.ForUser(7004, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);

        var owed = await CompleteLesson(tutor, studentId, PastDate(days: 10), paid: false);
        var upcoming = await CreateLesson(tutor, studentId, FutureDate(days: 3));

        var response = await app.Api.PostAs(tutor, "/lessons/settle", new { lessonIds = new[] { owed, upcoming } });

        // All or nothing: one lesson nobody has taught yet refuses the selection, and the debt that
        // WAS owed is still owed.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ProblemFields(response), f => f.Equals("LessonIds", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(250m, (await GetDetails(tutor, studentId)).Debt!.Amount);
    }

    [Fact]
    public async Task Another_tutors_debt_is_neither_readable_nor_settleable()
    {
        var mine = TelegramInitData.ForUser(7005, "Al");
        var theirs = TelegramInitData.ForUser(7006, "Bo");
        await SetProfile(mine);
        await SetProfile(theirs);

        var myStudent = await CreateStudent(mine, rate: 250m);
        var myLesson = await CompleteLesson(mine, myStudent, PastDate(days: 10), paid: false);

        // The other tutor reaches for both halves of the contract.
        var read = await app.Api.GetAs(theirs, $"/students/{myStudent}/debts");
        var settle = await app.Api.PostAs(theirs, "/lessons/settle", new { lessonIds = new[] { myLesson } });

        // The student is not found rather than forbidden — existence never leaks — and the lesson is
        // simply an id nobody can resolve, so the batch is refused and my money stays owed.
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, settle.StatusCode);
        Assert.Equal(250m, (await GetDetails(mine, myStudent)).Debt!.Amount);
    }

    [Fact]
    public async Task Debts_of_an_archived_student_are_still_served()
    {
        var tutor = TelegramInitData.ForUser(7007, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);
        var owed = await CompleteLesson(tutor, studentId, PastDate(days: 10), paid: false);

        var archive = await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var details = await GetDetails(tutor, studentId);
        var debts = await GetDebts(tutor, studentId);

        // The archive cascade keeps completed lessons precisely because the money outlives the
        // schedule — and the tutor can still be paid for them.
        Assert.Equal(250m, details.Debt!.Amount);
        Assert.Equal(owed, Assert.Single(debts.Lessons).Id);
        Assert.Equal(1, await Settle(tutor, [owed]));
        Assert.Null((await GetDetails(tutor, studentId)).Debt);
    }

    // ---- helpers ----

    private async Task SetProfile(string tutor, string zone = "Europe/Kyiv")
    {
        var resp = await app.Api.PutAs(tutor, "/profile", new { timeZoneId = zone });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<Guid> CreateStudent(string tutor, decimal rate)
    {
        var resp = await app.Api.PostAs(tutor, "/students", new { name = "Student", rate });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<StudentDto>())!.Id;
    }

    /// <summary>A one-off lesson at the student's own rate on <paramref name="date"/>.</summary>
    private async Task<Guid> CreateLesson(string tutor, Guid studentId, DateOnly date, string? topic = null)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date,
            startTimeLocal = "10:00:00",
            durationMinutes = 60,
            topic,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<CreateDto>())!.Lesson!.Id;
    }

    /// <summary>The same, then closed out as taught — paid or not.</summary>
    private async Task<Guid> CompleteLesson(
        string tutor, Guid studentId, DateOnly date, bool paid, string? topic = null)
    {
        var lessonId = await CreateLesson(tutor, studentId, date, topic);
        var patch = await app.Api.PatchAs(tutor, $"/lessons/{lessonId}", new { status = "Completed", isPaid = paid });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        return lessonId;
    }

    private async Task CreateWeeklySeries(string tutor, Guid studentId, DateOnly startMonday, string title)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = startMonday,
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
            repeat = new { weekdays = "Monday", title },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task SetStatus(string tutor, Guid lessonId, string status)
    {
        var resp = await app.Api.PatchAs(tutor, $"/lessons/{lessonId}", new { status });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<StudentDetailsDto> GetDetails(string tutor, Guid studentId)
    {
        var resp = await app.Api.GetAs(tutor, $"/students/{studentId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<StudentDetailsDto>())!;
    }

    private async Task<DebtsDto> GetDebts(string tutor, Guid studentId)
    {
        var resp = await app.Api.GetAs(tutor, $"/students/{studentId}/debts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<DebtsDto>())!;
    }

    /// <summary>Settles the given lessons and returns how many the API says it settled.</summary>
    private async Task<int> Settle(string tutor, Guid[] lessonIds)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons/settle", new { lessonIds });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SettleDto>())!.Settled;
    }

    private async Task<DashboardDto> GetDashboard(string tutor)
    {
        var resp = await app.Api.GetAs(tutor, "/reports/dashboard?period=week");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<DashboardDto>())!;
    }

    /// <summary>The form fields a ValidationProblem payload blames — its <c>errors</c> keys.</summary>
    private static async Task<IEnumerable<string>> ProblemFields(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Errors.Keys;

    /// <summary>The id of the tutor's single lesson on <paramref name="date"/>, read off the schedule.</summary>
    private async Task<Guid> LessonIdOn(string tutor, DateOnly date)
    {
        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var url = $"/lessons?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var resp = await app.Api.GetAs(tutor, url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var schedule = (await resp.Content.ReadFromJsonAsync<List<LessonDto>>())!;
        return Assert.Single(schedule).Id;
    }

    private static DateOnly PastDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-days);

    private static DateOnly FutureDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(days);

    /// <summary>A Monday comfortably in the future (so the occurrence is unambiguously ahead of us).</summary>
    private static DateOnly NextMonday()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        while (date.DayOfWeek != DayOfWeek.Monday)
            date = date.AddDays(1);
        return date;
    }

    private sealed record StudentDto(Guid Id);

    private sealed record LessonDto(Guid Id);

    private sealed record CreateDto(LessonDto? Lesson);

    private sealed record DebtDto(decimal Amount, int LessonsCount);

    private sealed record StudentDetailsDto(Guid Id, string Status, DebtDto? Debt);

    private sealed record DebtLessonDto(
        Guid Id, DateTimeOffset StartUtc, int DurationMinutes, decimal Price, string? Subject);

    private sealed record DebtsDto(List<DebtLessonDto> Lessons, decimal TotalAmount, int Count);

    private sealed record SettleDto(int Settled);

    private sealed record DashboardDebtDto(decimal Total, List<DashboardDebtorDto> Debtors);

    private sealed record DashboardDebtorDto(Guid StudentId, decimal Amount, int LessonsCount);

    private sealed record DashboardDto(DashboardDebtDto Debt);

    private sealed record ProblemDto(Dictionary<string, string[]> Errors);
}
