using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Profile;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Profile;

/// <summary>
/// Endpoint-level coverage for <c>PUT/GET /profile</c>'s notification settings and the
/// tomorrow-lessons hint the agenda-time bottom sheet reads.
/// </summary>
public class ProfileEndpointsTests
{
    private const long Tutor = 555;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // January: London sits on plain GMT (UTC+0), so local wall-clock times equal their UTC instants
    // and the fixtures below don't have to reason about DST at all.
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = Now;

    private readonly TutorContext _tenant = new();
    private readonly FakeTutorProfileRepository _profiles;
    private readonly FakeLessonRepository _lessons;
    private readonly FakeUnitOfWork _uow = new();
    private readonly FixedClock _clock = new(Now);
    private readonly Guid _studentId = Guid.NewGuid();

    public ProfileEndpointsTests()
    {
        _tenant.SetFromAuthentication(Tutor);
        _profiles = new FakeTutorProfileRepository(_tenant);
        _lessons = new FakeLessonRepository(_tenant);
    }

    [Fact]
    public async Task Put_NewProfile_DefaultsDaySummaryOnAndMorningAgendaOff()
    {
        // Arrange
        var request = new UpdateProfileRequest("Europe/London", null);

        // Act
        var result = await Put(request);

        // Assert
        var response = Ok(result);
        Assert.True(response.DaySummary);
        Assert.False(response.MorningAgenda);
        Assert.Equal("08:00", response.MorningAgendaAt);
    }

    [Theory]
    [InlineData("8:00")]
    [InlineData("25:00")]
    [InlineData("08:00:00")]
    public async Task Put_MorningAgendaAtMalformed_ReturnsValidationProblem(string malformed)
    {
        // Arrange
        var request = new UpdateProfileRequest("Europe/London", null, MorningAgendaAt: malformed);

        // Act
        var result = await Put(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("MorningAgendaAt", problem.Errors.Keys);
    }

    [Fact]
    public async Task Put_MorningAgendaAtValid_StoresTheLocalTime()
    {
        // Arrange
        var request = new UpdateProfileRequest("Europe/London", null, MorningAgenda: true, MorningAgendaAt: "07:30");

        // Act
        var result = await Put(request);

        // Assert
        var response = Ok(result);
        Assert.True(response.MorningAgenda);
        Assert.Equal("07:30", response.MorningAgendaAt);
    }

    [Fact]
    public async Task Put_NullNotificationFields_LeavesStoredSettingsUnchanged()
    {
        // Arrange
        await Put(new UpdateProfileRequest(
            "Europe/London", null, DaySummary: false, MorningAgenda: true, MorningAgendaAt: "09:15"));

        // Act — a follow-up save that touches only the time zone.
        var result = await Put(new UpdateProfileRequest("Europe/London", null));

        // Assert
        var response = Ok(result);
        Assert.False(response.DaySummary);
        Assert.True(response.MorningAgenda);
        Assert.Equal("09:15", response.MorningAgendaAt);
    }

    [Fact]
    public async Task Get_LessonsTomorrow_CountsOnlyNonCancelledLessonsStartingTomorrow()
    {
        // Arrange
        _profiles.Add(TutorProfile.Create(Tutor, London, CreatedAt).Value);
        AddLesson(new DateTimeOffset(2026, 1, 16, 10, 0, 0, TimeSpan.Zero)); // tomorrow, kept
        AddLesson(new DateTimeOffset(2026, 1, 16, 14, 0, 0, TimeSpan.Zero), cancel: true); // tomorrow, cancelled
        AddLesson(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)); // today
        AddLesson(new DateTimeOffset(2026, 1, 17, 10, 0, 0, TimeSpan.Zero)); // day after tomorrow

        // Act
        var result = await Get();

        // Assert
        Assert.Equal(1, Ok(result).TomorrowLessonsCount);
    }

    [Fact]
    public async Task Get_LessonAcrossMidnight_CountsItOnTheDayItStartsOn()
    {
        // Arrange
        _profiles.Add(TutorProfile.Create(Tutor, London, CreatedAt).Value);
        // Starts tomorrow at 23:30 local, ends the day after — belongs to tomorrow (the day it starts).
        AddLesson(new DateTimeOffset(2026, 1, 16, 23, 30, 0, TimeSpan.Zero));
        // Starts today at 23:30 local, ends tomorrow — belongs to today, even though it spills into
        // tomorrow's UTC window.
        AddLesson(new DateTimeOffset(2026, 1, 15, 23, 30, 0, TimeSpan.Zero));

        // Act
        var result = await Get();

        // Assert
        Assert.Equal(1, Ok(result).TomorrowLessonsCount);
    }

    private Lesson AddLesson(DateTimeOffset startUtc, bool cancel = false)
    {
        var lesson = Lesson.Create(_studentId, startUtc, 60, 100m, CreatedAt).Value.OwnedBy(Tutor);
        if (cancel)
            lesson.ChangeStatus(LessonStatus.Cancelled);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Task<Results<Ok<ProfileResponse>, ValidationProblem>> Put(UpdateProfileRequest request) =>
        Endpoints.Put(request, _profiles, _tenant, _uow, _lessons, _clock, default);

    private Task<Results<Ok<ProfileResponse>, NotFound>> Get() =>
        Endpoints.Get(_profiles, _lessons, _clock, default);

    private static ProfileResponse Ok(Results<Ok<ProfileResponse>, ValidationProblem> result) =>
        Assert.IsType<Ok<ProfileResponse>>(result.Result).Value!;

    private static ProfileResponse Ok(Results<Ok<ProfileResponse>, NotFound> result) =>
        Assert.IsType<Ok<ProfileResponse>>(result.Result).Value!;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
