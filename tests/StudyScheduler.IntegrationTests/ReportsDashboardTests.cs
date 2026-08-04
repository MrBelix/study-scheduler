using System.Net.Http.Json;
using System.Text.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end tests for <c>GET /reports/dashboard</c> over the real stack. The arithmetic is pinned
/// by the unit tests; what only PostgreSQL can prove is that the debt ledger's query translates and
/// that the payload serializes in the shape the Money screen consumes.
/// </summary>
[Collection(nameof(AppCollection))]
public class ReportsDashboardTests(AppFixture app)
{
    [Fact]
    public async Task Dashboard_reports_unpaid_completed_lessons_as_all_time_debt()
    {
        var tutor = TelegramInitData.ForUser(6001, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 250m);

        // Two lessons well before the requested week, taught but never paid for…
        await CompleteLesson(tutor, studentId, PastDate(days: 120), paid: false);
        await CompleteLesson(tutor, studentId, PastDate(days: 60), paid: false);
        // …and one that was settled, which is no debt at all.
        await CompleteLesson(tutor, studentId, PastDate(days: 30), paid: true);

        var dashboard = await GetDashboard(tutor, "week");

        Assert.Equal(500m, dashboard.Debt.Total);
        var debtor = Assert.Single(dashboard.Debt.Debtors);
        Assert.Equal(studentId, debtor.StudentId);
        Assert.Equal("Student", debtor.Name);
        Assert.Equal(2, debtor.LessonsCount);
        Assert.Equal(500m, debtor.Amount);
        // The oldest of the two, not of every unpaid-looking row.
        Assert.Equal(PastDate(days: 120), DateOnly.FromDateTime(debtor.OldestUtc.UtcDateTime));
    }

    [Fact]
    public async Task Dashboard_counts_income_and_lessons_of_the_requested_period_only()
    {
        var tutor = TelegramInitData.ForUser(6002, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor, rate: 400m);

        var thisMonth = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var anchor = new DateOnly(thisMonth.Year, thisMonth.Month, 15);
        await CompleteLesson(tutor, studentId, anchor, paid: true);
        // Two months back: outside both the period and its baseline.
        await CompleteLesson(tutor, studentId, anchor.AddMonths(-2), paid: true);

        var dashboard = await GetDashboard(tutor, "month", anchor);

        Assert.Equal(new DateOnly(anchor.Year, anchor.Month, 1), dashboard.Period.From);
        Assert.Equal(400m, dashboard.Income.Actual);
        Assert.Equal(400m, dashboard.Income.Expected);
        Assert.Equal(0m, dashboard.Income.Previous);
        Assert.Equal(1, dashboard.Lessons.Completed);
        var earner = Assert.Single(dashboard.PerStudent);
        Assert.Equal(400m, earner.Income);
    }

    [Fact]
    public async Task Dashboard_serializes_dates_as_plain_days_and_buckets_the_whole_period()
    {
        var tutor = TelegramInitData.ForUser(6003, "Al");
        await SetProfile(tutor);

        var response = await app.Api.GetAs(tutor, "/reports/dashboard?period=week&anchor=2026-07-08");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("2026-07-06", root.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal("2026-07-12", root.GetProperty("period").GetProperty("to").GetString());

        // A week is charted day by day; every key the client reads is present.
        var buckets = root.GetProperty("buckets");
        Assert.Equal(7, buckets.GetArrayLength());
        Assert.Equal("2026-07-06", buckets[0].GetProperty("from").GetString());
        Assert.Equal("2026-07-06", buckets[0].GetProperty("to").GetString());
        Assert.Equal(0, buckets[0].GetProperty("completedCount").GetInt32());
        Assert.Equal(0, buckets[0].GetProperty("scheduledCount").GetInt32());

        Assert.Equal(0m, root.GetProperty("income").GetProperty("expected").GetDecimal());
        Assert.Equal(0, root.GetProperty("weeklyLoad").GetProperty("lessonsInPeriod").GetInt32());
        Assert.Empty(root.GetProperty("perStudent").EnumerateArray());
    }

    [Theory]
    [InlineData("/reports/dashboard")]
    [InlineData("/reports/dashboard?period=year")]
    [InlineData("/reports/dashboard?period=week&anchor=08-07-2026")]
    public async Task Dashboard_with_an_unusable_query_returns_validation_problem(string url)
    {
        var tutor = TelegramInitData.ForUser(6004, "Al");
        await SetProfile(tutor);

        var response = await app.Api.GetAs(tutor, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_without_a_profile_returns_validation_problem()
    {
        var tutor = TelegramInitData.ForUser(6005, "Al");

        var response = await app.Api.GetAs(tutor, "/reports/dashboard?period=week");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_requires_authentication()
    {
        var response = await app.Api.GetAsync("/reports/dashboard?period=week");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_never_leaks_another_tutors_money()
    {
        var mine = TelegramInitData.ForUser(6006, "Al");
        var theirs = TelegramInitData.ForUser(6007, "Bo");
        await SetProfile(mine);
        await SetProfile(theirs);
        await CompleteLesson(theirs, await CreateStudent(theirs, rate: 900m), PastDate(days: 10), paid: false);

        var dashboard = await GetDashboard(mine, "quarter");

        Assert.Equal(0m, dashboard.Debt.Total);
        Assert.Empty(dashboard.Debt.Debtors);
        Assert.Equal(0m, dashboard.Income.Actual);
    }

    // ---- helpers ----

    private async Task<DashboardDto> GetDashboard(string tutor, string period, DateOnly? anchor = null)
    {
        var url = $"/reports/dashboard?period={period}"
            + (anchor is { } a ? $"&anchor={a:yyyy-MM-dd}" : string.Empty);
        var response = await app.Api.GetAs(tutor, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DashboardDto>())!;
    }

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

    /// <summary>A one-off lesson on <paramref name="date"/>, then closed out as taught.</summary>
    private async Task CompleteLesson(string tutor, Guid studentId, DateOnly date, bool paid)
    {
        var create = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date,
            startTimeLocal = "10:00:00",
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lessonId = (await create.Content.ReadFromJsonAsync<CreateDto>())!.Lesson!.Id;

        var patch = await app.Api.PatchAs(tutor, $"/lessons/{lessonId}", new { status = "Completed", isPaid = paid });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }

    private static DateOnly PastDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-days);

    private sealed record DashboardDto(
        PeriodDto Period,
        IncomeDto Income,
        DebtDto Debt,
        LessonCountsDto Lessons,
        WeeklyLoadDto WeeklyLoad,
        List<BucketDto> Buckets,
        List<StudentIncomeDto> PerStudent);

    private sealed record PeriodDto(DateOnly From, DateOnly To);

    private sealed record IncomeDto(decimal Actual, decimal Expected, decimal Previous);

    private sealed record DebtDto(decimal Total, List<DebtorDto> Debtors);

    private sealed record DebtorDto(
        Guid StudentId, string Name, decimal Amount, int LessonsCount, DateTimeOffset OldestUtc);

    private sealed record LessonCountsDto(int Completed, int Scheduled, int Cancelled);

    private sealed record WeeklyLoadDto(decimal Hours, int LessonsInPeriod);

    private sealed record BucketDto(DateOnly From, DateOnly To, int CompletedCount, int ScheduledCount);

    private sealed record StudentIncomeDto(Guid StudentId, string Name, decimal Income);

    private sealed record LessonDto(Guid Id, decimal Price, bool IsPaid);

    private sealed record CreateDto(LessonDto? Lesson, object? Series);

    private sealed record StudentDto(Guid Id, string Name, decimal Rate);
}
