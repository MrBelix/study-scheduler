using System.Text.Json;
using System.Text.Json.Serialization;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The wire contract of the series write requests, deserialized exactly as minimal APIs do it.
/// <c>keepCustomized</c> defaults to keeping, and a client that never heard of the flag must not
/// silently get the destructive branch — which is what these pin down.
/// </summary>
public class SeriesRequestContractsTests
{
    /// <summary>Exactly what Program.cs hands minimal APIs: web defaults plus string enums.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Deserialize_UpdateRequestWithoutKeepCustomized_DefaultsToKeepingThem()
    {
        // Arrange
        const string json = """{"endDate":"2026-07-20"}""";

        // Act
        var request = JsonSerializer.Deserialize<UpdateLessonSeriesRequest>(json, Web)!;

        // Assert
        Assert.True(request.KeepCustomized);
        Assert.False(request.ClearEndDate);
        Assert.Equal(new DateOnly(2026, 7, 20), request.EndDate);
        // Absent schedule fields mean "not provided", never "clear it".
        Assert.Null(request.Weekdays);
        Assert.Null(request.StartTimeLocal);
        Assert.Null(request.DurationMinutes);
    }

    [Fact]
    public void Deserialize_UpdateRequestWithScheduleFields_BindsTheWholeSchedule()
    {
        // Arrange
        const string json = """
            {"weekdays":"Monday, Thursday","startTimeLocal":"18:30:00","durationMinutes":90,"keepCustomized":false}
            """;

        // Act
        var request = JsonSerializer.Deserialize<UpdateLessonSeriesRequest>(json, Web)!;

        // Assert
        Assert.Equal(Weekdays.Monday | Weekdays.Thursday, request.Weekdays);
        Assert.Equal(new TimeOnly(18, 30), request.StartTimeLocal);
        Assert.Equal(90, request.DurationMinutes);
        Assert.False(request.KeepCustomized);
    }

    [Fact]
    public void Deserialize_CancelRequestWithoutFields_DefaultsToKeepingCustomized()
    {
        // Arrange
        const string json = "{}";

        // Act
        var request = JsonSerializer.Deserialize<CancelLessonSeriesRequest>(json, Web)!;

        // Assert
        Assert.True(request.KeepCustomized);
    }
}
