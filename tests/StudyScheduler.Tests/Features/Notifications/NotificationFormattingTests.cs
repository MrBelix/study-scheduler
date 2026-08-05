using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationFormattingTests
{
    [Fact]
    public void Escape_NameWithMarkupCharacters_EncodesThem()
    {
        // Arrange
        const string name = "A & B <script>";

        // Act
        var escaped = NotificationFormatting.Escape(name);

        // Assert
        Assert.DoesNotContain("<script>", escaped);
        Assert.Contains("&amp;", escaped);
        Assert.Contains("&lt;", escaped);
    }

    [Fact]
    public void Escape_Null_ReturnsEmpty()
    {
        // Arrange / Act
        var escaped = NotificationFormatting.Escape(null);

        // Assert
        Assert.Equal(string.Empty, escaped);
    }

    [Fact]
    public void Time_LocalInstant_FormatsAsHhMmWithoutPreposition()
    {
        // Arrange
        var local = new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.FromHours(3));

        // Act
        var text = NotificationFormatting.Time(local);

        // Assert
        Assert.Equal("14:00", text);
    }

    [Fact]
    public void FirstName_MultiWordName_ReturnsFirstWordOnly()
    {
        // Arrange / Act
        var first = NotificationFormatting.FirstName("Іван Петренко");

        // Assert
        Assert.Equal("Іван", first);
    }

    [Fact]
    public void FirstName_LongFirstWord_TruncatesToFourteenTextElements()
    {
        // Arrange
        const string name = "Пантелеймонович Петренко";

        // Act
        var first = NotificationFormatting.FirstName(name);

        // Assert
        Assert.Equal(14, new System.Globalization.StringInfo(first).LengthInTextElements);
    }

    [Fact]
    public void ShortName_SingleLastName_ReturnsFirstNamePlusInitial()
    {
        // Arrange / Act
        var shortName = NotificationFormatting.ShortName("Іван Петренко");

        // Assert
        Assert.Equal("Іван П.", shortName);
    }

    [Fact]
    public void ShortName_HyphenatedDoubleLastName_ReturnsBothInitials()
    {
        // Arrange / Act
        var shortName = NotificationFormatting.ShortName("Олександра Вишневецька-Богуславська");

        // Assert
        Assert.Equal("Олександра В.-Б.", shortName);
    }

    [Fact]
    public void ShortName_NoLastName_ReturnsNameUnchanged()
    {
        // Arrange / Act
        var shortName = NotificationFormatting.ShortName("Оленка");

        // Assert
        Assert.Equal("Оленка", shortName);
    }

    [Fact]
    public void Truncate_ValueUnderLimit_ReturnsUnchanged()
    {
        // Arrange / Act
        var result = NotificationFormatting.Truncate("Іван", 14);

        // Assert
        Assert.Equal("Іван", result);
    }

    [Fact]
    public void Truncate_ValueOverLimit_CutsToTextElementCount()
    {
        // Arrange / Act
        var result = NotificationFormatting.Truncate("Пантелеймонович", 5);

        // Assert
        Assert.Equal(5, new System.Globalization.StringInfo(result).LengthInTextElements);
    }

    [Fact]
    public void Money_IntegralAmount_HasNoDecimalsAndThousandsSeparator()
    {
        // Arrange / Act
        var text = NotificationFormatting.Money(2400m);

        // Assert
        Assert.Equal($"2{' '}400 ₴", text);
    }

    [Fact]
    public void Money_ZeroAmount_RendersZero()
    {
        // Arrange / Act
        var text = NotificationFormatting.Money(0m);

        // Assert
        Assert.Equal("0 ₴", text);
    }

    [Theory]
    [InlineData(1, "урок")]
    [InlineData(2, "уроки")]
    [InlineData(4, "уроки")]
    [InlineData(5, "уроків")]
    [InlineData(11, "уроків")]
    [InlineData(21, "урок")]
    [InlineData(22, "уроки")]
    [InlineData(25, "уроків")]
    public void Plural_Ukrainian_PicksCorrectForm(int n, string expected)
    {
        // Arrange / Act
        var word = NotificationFormatting.Plural(AppLanguage.Uk, n, "урок", "уроки", "уроків", "lesson", "lessons");

        // Assert
        Assert.Equal(expected, word);
    }

    [Theory]
    [InlineData(1, "lesson")]
    [InlineData(2, "lessons")]
    [InlineData(0, "lessons")]
    public void Plural_English_PicksCorrectForm(int n, string expected)
    {
        // Arrange / Act
        var word = NotificationFormatting.Plural(AppLanguage.En, n, "урок", "уроки", "уроків", "lesson", "lessons");

        // Assert
        Assert.Equal(expected, word);
    }

    [Fact]
    public void WeekdayAndDate_Ukrainian_UsesGenitiveMonth()
    {
        // Arrange — 2026-08-05 is a Wednesday.
        var date = new DateOnly(2026, 8, 5);

        // Act
        var text = NotificationFormatting.WeekdayAndDate(AppLanguage.Uk, date);

        // Assert
        Assert.Equal("середа, 5 серпня", text);
    }

    [Fact]
    public void WeekdayAndDate_English_UsesFullWeekdayAndMonth()
    {
        // Arrange
        var date = new DateOnly(2026, 8, 5);

        // Act
        var text = NotificationFormatting.WeekdayAndDate(AppLanguage.En, date);

        // Assert
        Assert.Equal("Wednesday, 5 August", text);
    }

    [Fact]
    public void DayWord_SameDate_ReturnsToday()
    {
        // Arrange
        var date = new DateOnly(2026, 8, 5);

        // Act
        var word = NotificationFormatting.DayWord(AppLanguage.Uk, date, date);

        // Assert
        Assert.Equal("сьогодні", word);
    }

    [Fact]
    public void DayWord_NextDate_ReturnsTomorrow()
    {
        // Arrange
        var today = new DateOnly(2026, 8, 5);

        // Act
        var word = NotificationFormatting.DayWord(AppLanguage.Uk, today, today.AddDays(1));

        // Assert
        Assert.Equal("завтра", word);
    }

    [Fact]
    public void DayWord_FarDate_ReturnsAbbreviatedWeekdayAndMonth()
    {
        // Arrange — 2026-08-12 is a Wednesday; today is far enough away it must spell out the date.
        var today = new DateOnly(2026, 8, 1);
        var other = new DateOnly(2026, 8, 12);

        // Act
        var word = NotificationFormatting.DayWord(AppLanguage.Uk, today, other);

        // Assert
        Assert.Equal("ср 12 серп.", word);
    }

    [Fact]
    public void Width_MultiCharacterEmojiLabel_CountsTextElementsNotChars()
    {
        // Arrange
        const string label = "✅ Проведено";

        // Act
        var width = NotificationFormatting.Width(label);

        // Assert
        Assert.Equal(new System.Globalization.StringInfo(label).LengthInTextElements, width);
    }
}
