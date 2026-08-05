using System.Globalization;
using System.Net;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// The formatting primitives every template composes with. No literal wording lives here — only
/// escaping, truncation, number/date shaping and pluralization rules — so <see cref="NotificationCopy"/>
/// stays the single home for actual phrases.
/// </summary>
internal static class NotificationFormatting
{
    private static readonly string[] UkWeekdaysFull =
        ["неділя", "понеділок", "вівторок", "середа", "четвер", "п'ятниця", "субота"];

    private static readonly string[] UkMonthsGenitive =
        ["січня", "лютого", "березня", "квітня", "травня", "червня",
         "липня", "серпня", "вересня", "жовтня", "листопада", "грудня"];

    private static readonly string[] UkWeekdayAbbr = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    private static readonly string[] UkMonthAbbr =
        ["січ", "лют", "бер", "кві", "тра", "чер", "лип", "серп", "вер", "жов", "лис", "гру"];

    /// <summary>HTML-encodes every user-supplied string before it touches a <c>&lt;b&gt;</c> tag.</summary>
    public static string Escape(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;

    /// <summary>"HH:mm", invariant — never "о 14:00" (spec §1: time is never prefixed).</summary>
    public static string Time(DateTimeOffset local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The first whitespace-delimited word of <paramref name="name"/>, cut to 14 text elements.</summary>
    public static string FirstName(string name)
    {
        var words = (name ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Truncate(words.Length > 0 ? words[0] : string.Empty, 14);
    }

    /// <summary>
    /// "Іван Петренко" → "Іван П."; "Олександра Вишневецька-Богуславська" → "Олександра В.-Б." — the
    /// last name is reduced to its hyphen-separated initials.
    /// </summary>
    public static string ShortName(string name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;
        if (parts.Length == 1)
            return parts[0];

        var initials = string.Join(
            "-",
            parts[1].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Initial));
        return $"{parts[0]} {initials}";
    }

    private static string Initial(string segment)
    {
        if (segment.Length == 0)
            return string.Empty;

        var enumerator = StringInfo.GetTextElementEnumerator(segment);
        return enumerator.MoveNext() ? $"{enumerator.GetTextElement()}." : string.Empty;
    }

    /// <summary>Cuts <paramref name="value"/> to at most <paramref name="maxTextElements"/> text elements.</summary>
    public static string Truncate(string value, int maxTextElements)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var info = new StringInfo(value);
        return info.LengthInTextElements <= maxTextElements
            ? value
            : info.SubstringByTextElements(0, maxTextElements);
    }

    /// <summary>Thin-space thousands + "₴"; no decimals when the value is integral ("2 400 ₴").</summary>
    public static string Money(decimal amount)
    {
        var isIntegral = amount == decimal.Truncate(amount);
        var nfi = new NumberFormatInfo
        {
            NumberGroupSeparator = " ",
            NumberGroupSizes = [3],
            NumberDecimalSeparator = ",",
        };
        return $"{amount.ToString(isIntegral ? "N0" : "N2", nfi)} ₴";
    }

    /// <summary>
    /// Ukrainian three-form plural (1 / 2-4 / 5+) or the English two-form. Returns the bare word — the
    /// caller composes "{n} {word}".
    /// </summary>
    public static string Plural(
        AppLanguage lang, int n, string ukOne, string ukFew, string ukMany, string enOne, string enMany)
    {
        if (lang == AppLanguage.En)
            return n == 1 ? enOne : enMany;

        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11)
            return ukOne;
        if (mod10 is >= 2 and <= 4 && (mod100 < 10 || mod100 >= 20))
            return ukFew;
        return ukMany;
    }

    /// <summary>"вівторок, 5 серпня" / "Tuesday, 5 August" — full weekday and date, genitive month in Ukrainian.</summary>
    public static string WeekdayAndDate(AppLanguage lang, DateOnly date) => lang switch
    {
        AppLanguage.En =>
            $"{CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(date.DayOfWeek)}, {date.Day} " +
            $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month)}",
        _ => $"{UkWeekdaysFull[(int)date.DayOfWeek]}, {date.Day} {UkMonthsGenitive[date.Month - 1]}",
    };

    /// <summary>"сьогодні" / "завтра" / "вт 12 серп." (spec N2) — relative or abbreviated absolute day.</summary>
    public static string DayWord(AppLanguage lang, DateOnly today, DateOnly other)
    {
        if (other == today)
            return lang == AppLanguage.En ? "today" : "сьогодні";
        if (other == today.AddDays(1))
            return lang == AppLanguage.En ? "tomorrow" : "завтра";

        return lang == AppLanguage.En
            ? $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(other.DayOfWeek)} {other.Day} " +
              $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(other.Month)}"
            : $"{UkWeekdayAbbr[(int)other.DayOfWeek]} {other.Day} {UkMonthAbbr[other.Month - 1]}.";
    }

    /// <summary>Text-element width of a button label — used only by the row-pairing assertion.</summary>
    public static int Width(string label) => new StringInfo(label).LengthInTextElements;
}
