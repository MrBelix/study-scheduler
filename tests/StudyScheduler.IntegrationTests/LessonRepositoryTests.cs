using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// Repository-level regression tests against the containerized SQL Server, for EF behavior the
/// HTTP surface can't reach deterministically — here, the materialization race on the unique
/// <c>(SeriesId, OccurrenceDate)</c> index.
/// </summary>
[Collection(nameof(AppCollection))]
public class LessonRepositoryTests(AppFixture app)
{
    private static readonly DateTimeOffset BaseUtc =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(60);

    /// <summary>
    /// The webhook double-tap: after losing the unique-index race, the handler calls
    /// <c>IUnitOfWork.DiscardChanges</c> and retries in the same scope by updating the winning
    /// row. Without the discard the loser stays tracked as Added and every later save in the
    /// scope retries the doomed insert.
    /// </summary>
    [Fact]
    public async Task Losing_the_materialization_race_leaves_the_context_usable_for_the_retry()
    {
        var tutorId = 3401L;
        var tutor = TelegramInitData.ForUser(tutorId, "Alice");
        var student = await ReadAs<StudentDto>(
            await app.Api.PostAs(tutor, "/students", new { name = "Kid", rate = 100m }), HttpStatusCode.Created);
        Assert.Equal(
            HttpStatusCode.OK,
            (await app.Api.PutAs(tutor, "/profile", new { timeZoneId = "Europe/Kyiv" })).StatusCode);

        var occurrenceDate = DateOnly.FromDateTime(BaseUtc.UtcDateTime);
        var series = await ReadAs<SeriesDto>(await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId = student.Id,
            startDate = occurrenceDate,
            weekdays = occurrenceDate.DayOfWeek.ToString(),
            startTimeLocal = new TimeOnly(10, 0),
            durationMinutes = 60,
        }), HttpStatusCode.Created);

        // The scope that will lose the race (stands in for one webhook request).
        await using var db = app.CreateDbContext();
        var repo = new EfLessonRepository(db);
        var uow = new EfUnitOfWork(db);

        // The winning press materializes the slot from another scope first.
        await using (var winnerDb = app.CreateDbContext())
        {
            var winnerRepo = new EfLessonRepository(winnerDb);
            var winnerUow = new EfUnitOfWork(winnerDb);
            winnerRepo.Add(NewSlotLesson(tutorId, student.Id, series.Id, occurrenceDate));
            await winnerUow.SaveChangesAsync();
        }

        repo.Add(NewSlotLesson(tutorId, student.Id, series.Id, occurrenceDate));
        await Assert.ThrowsAsync<DbUpdateException>(() => uow.SaveChangesAsync());

        // The handler's recovery path: discard the doomed insert, then apply the action to the
        // winning row in the same scope.
        uow.DiscardChanges();
        var winner = await repo.GetBySeriesOccurrenceAsync(series.Id, occurrenceDate);
        Assert.NotNull(winner);
        winner!.ChangeStatus(LessonStatus.Completed);
        repo.Update(winner);
        await uow.SaveChangesAsync();

        await using var verifyDb = app.CreateDbContext();
        var persisted = await verifyDb.Lessons
            .AsNoTracking()
            .SingleAsync(l => l.SeriesId == series.Id && l.OccurrenceDate == occurrenceDate);
        Assert.Equal(LessonStatus.Completed, persisted.Status);
    }

    private static Lesson NewSlotLesson(long tutorId, Guid studentId, Guid seriesId, DateOnly occurrenceDate) =>
        Lesson.Create(
            tutorId, studentId, BaseUtc.AddHours(10), 60, 100m, DateTimeOffset.UtcNow,
            seriesId: seriesId, occurrenceDate: occurrenceDate).Value;

    private static async Task<T> ReadAs<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record StudentDto(Guid Id);

    private sealed record SeriesDto(Guid Id);
}
