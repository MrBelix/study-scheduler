using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// The exact text, keyboard, snapshot and hash one <see cref="NotificationRenderer"/> call produced —
/// everything a caller needs to send or edit a message and to persist what it was rendered from.
/// </summary>
public sealed record RenderedMessage(
    string Text, IReadOnlyList<NotificationButtonRow> Rows, RenderedSnapshot Snapshot, string ContentHash);

/// <summary>
/// The single seam that turns a view (<see cref="ReminderView"/>, <see cref="AgendaView"/>,
/// <see cref="SummaryView"/>) into the exact HTML text, button rows, <see cref="RenderedSnapshot"/> and
/// <see cref="ContentHash"/> the design spec names. Every literal word comes from
/// <see cref="NotificationCopy"/>; every shaping primitive from <see cref="NotificationFormatting"/>.
/// <see cref="ContentHash"/> is computed at the END of each method from the FINAL text and keyboard —
/// there is no public hashing function and nothing hashes an input field, so text and hash cannot drift
/// apart by construction.
/// </summary>
public sealed class NotificationRenderer(IOptions<NotificationsOptions> options)
{
    public RenderedMessage Reminder(ReminderView view)
    {
        var lang = view.Lang;
        var firstName = NotificationFormatting.Escape(NotificationFormatting.FirstName(view.StudentName));
        var studentName = NotificationFormatting.Escape(view.StudentName);
        var start = NotificationFormatting.Time(view.StartLocal);
        var end = NotificationFormatting.Time(view.EndLocal);
        var topic = string.IsNullOrWhiteSpace(view.Topic) ? null : NotificationFormatting.Escape(view.Topic);
        var miniAppUrl = options.Value.MiniAppUrl;

        var lines = new List<string>();
        var rows = new List<NotificationButtonRow>();
        string phaseTag;

        switch (view.Phase)
        {
            case ReminderPhase.BeforeStart:
                phaseTag = nameof(ReminderPhase.BeforeStart);
                lines.Add(NotificationCopy.ReminderHeader(lang, view.RemindMinutes, firstName, start));
                lines.Add(studentName);
                if (topic is not null)
                    lines.Add(topic);
                lines.Add(NotificationCopy.ReminderTimeRange(lang, start, end, view.DurationMinutes));

                if (!string.IsNullOrEmpty(miniAppUrl))
                {
                    rows.Add(Row(NotificationCopy.OpenButton(lang), url: $"{miniAppUrl}?startapp=lesson-{view.LessonId:N}"));
                    rows.Add(Row(NotificationCopy.RescheduleButton(lang), url: $"{miniAppUrl}?startapp=lesson-{view.LessonId:N}-reschedule"));
                }
                rows.Add(Row(NotificationCopy.CancelLessonButton(lang), callback: $"x:{view.LessonId:N}"));
                break;

            case ReminderPhase.Moved:
                phaseTag = nameof(ReminderPhase.Moved);
                lines.Add(NotificationCopy.MovedHeader(lang, firstName, start));
                lines.Add(studentName);

                // The old slot is described relative to the day the tutor is actually reading the
                // message on — NowLocal — not the new start's own date. A same-day-to-tomorrow move
                // read today must still say "було сьогодні", not an absolute weekday.
                var oldStartLocal = view.OriginalStartLocal ?? view.StartLocal;
                var referenceDay = DateOnly.FromDateTime(view.NowLocal.Date);
                var oldDay = DateOnly.FromDateTime(oldStartLocal.Date);
                lines.Add(NotificationCopy.MovedWasLine(
                    lang, NotificationFormatting.DayWord(lang, referenceDay, oldDay), NotificationFormatting.Time(oldStartLocal)));

                if (!string.IsNullOrEmpty(miniAppUrl))
                    rows.Add(Row(NotificationCopy.OpenLessonButton(lang), url: $"{miniAppUrl}?startapp=lesson-{view.LessonId:N}"));
                break;

            case ReminderPhase.Cancelled:
                phaseTag = nameof(ReminderPhase.Cancelled);
                lines.Add(NotificationCopy.CancelledHeader(lang, firstName, start));
                if (view.WasPaidBeforeChange && view.Price > 0)
                    lines.Add(NotificationCopy.PaymentStaysLine(lang, NotificationFormatting.Money(view.Price)));
                break;

            case ReminderPhase.Completed:
                phaseTag = nameof(ReminderPhase.Completed);
                lines.Add(NotificationCopy.CompletedHeader(lang, firstName, start));
                if (!view.IsPaid && view.Price > 0)
                    lines.Add(NotificationCopy.NotPaidLine(lang, NotificationFormatting.Money(view.Price)));
                break;

            case ReminderPhase.Started:
                phaseTag = nameof(ReminderPhase.Started);
                lines.Add(NotificationCopy.StartedHeader(lang, firstName, start));
                if (topic is not null)
                    lines.Add(NotificationCopy.StartedTopicLine(lang, topic, end));

                rows.Add(Row(NotificationCopy.DoneButton(lang), callback: $"c:{view.LessonId:N}"));
                if (view.Price > 0)
                    rows.Add(Row(NotificationCopy.DonePaidButton(lang), callback: $"p:{view.LessonId:N}"));
                break;

            case ReminderPhase.Removed:
                phaseTag = nameof(ReminderPhase.Removed);
                lines.Add(NotificationCopy.RemovedHeader(lang, firstName, start));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(view), view.Phase, null);
        }

        var text = string.Join('\n', lines);
        var snapshot = new RenderedSnapshot(
            lang.ToCode(),
            NotificationKind.Reminder,
            new ReminderSnapshot(
                view.LessonId,
                (view.OriginalStartLocal ?? view.StartLocal).ToUniversalTime(),
                view.StartLocal.ToUniversalTime(),
                view.DurationMinutes,
                view.RemindMinutes,
                view.StudentName,
                view.Topic,
                view.Price,
                view.IsPaid,
                ToLessonStatus(view.Phase),
                phaseTag),
            null);

        return new RenderedMessage(text, rows, snapshot, Hash(text, rows));
    }

    public RenderedMessage Agenda(AgendaView view)
    {
        var lang = view.Lang;
        var miniAppUrl = options.Value.MiniAppUrl;
        var liveLines = view.Lines.Where(l => l.Status != LessonStatus.Cancelled).ToList();
        var compact = view.Lines.Count > 6;

        var lines = new List<string>();
        var rows = new List<NotificationButtonRow>();
        var showScheduleButton = true;

        if (view.Lines.Count > 0 && liveLines.Count == 0)
        {
            // A3 — every lesson of the day was cancelled. The rows stay so "free" does not read as a bug.
            lines.Add(NotificationCopy.AgendaHeaderFree(
                lang, view.Lines.Count, NotificationCopy.LessonWord(lang, view.Lines.Count)));
            foreach (var line in view.Lines.OrderBy(l => l.StartLocal))
                lines.Add(RenderDayLine(lang, line, compact: false));
            showScheduleButton = false;
        }
        else if (view.Lines.Count == 1)
        {
            // A3 — a single lesson gets its own template, no list.
            var only = view.Lines[0];
            var firstName = NotificationFormatting.Escape(NotificationFormatting.FirstName(only.StudentName));
            var start = NotificationFormatting.Time(only.StartLocal);
            var end = NotificationFormatting.Time(only.EndLocal);
            lines.Add(NotificationCopy.AgendaHeaderOne(lang, firstName, start));

            var topicSuffix = string.IsNullOrWhiteSpace(only.Topic)
                ? string.Empty
                : $" · {NotificationFormatting.Escape(only.Topic)}";
            lines.Add($"{NotificationFormatting.WeekdayAndDate(lang, view.LocalDate)} · {start}–{end}{topicSuffix}");
        }
        else
        {
            // A1 — the usual multi-lesson agenda.
            var first = NotificationFormatting.Time(liveLines.Min(l => l.StartLocal));
            var last = NotificationFormatting.Time(liveLines.Max(l => l.EndLocal));
            lines.Add(NotificationCopy.AgendaHeaderMulti(
                lang, liveLines.Count, NotificationCopy.LessonWord(lang, liveLines.Count), first, last));

            var sum = liveLines.Sum(l => l.Price);
            var weekdayAndDate = NotificationFormatting.WeekdayAndDate(lang, view.LocalDate);
            lines.Add(sum > 0 ? $"{weekdayAndDate} · {NotificationFormatting.Money(sum)}" : weekdayAndDate);
            lines.Add(string.Empty);

            foreach (var line in view.Lines.OrderBy(l => l.StartLocal))
                lines.Add(RenderDayLine(lang, line, compact));
        }

        var deltaLine = BuildAgendaDeltaLine(lang, view);
        if (deltaLine is not null)
            lines.Add(deltaLine);

        if (showScheduleButton && !string.IsNullOrEmpty(miniAppUrl))
            rows.Add(Row(NotificationCopy.OpenScheduleButton(lang), url: $"{miniAppUrl}?startapp=schedule-{view.LocalDate:yyyy-MM-dd}"));

        var text = string.Join('\n', lines);
        var snapshot = new RenderedSnapshot(
            lang.ToCode(),
            NotificationKind.DayAgenda,
            null,
            new DaySnapshot(
                view.LocalDate,
                [.. view.Lines.Select(ToDayLineSnapshot)],
                view.BaselineLessonIds.Count > 0 ? view.BaselineLessonIds : [.. view.Lines.Select(l => l.LessonId)],
                view.BaselineStarts.Count > 0
                    ? view.BaselineStarts
                    : view.Lines.ToDictionary(l => l.LessonId, l => l.StartLocal.ToUniversalTime()),
                view.UpdatedAtLocal?.ToUniversalTime()));

        return new RenderedMessage(text, rows, snapshot, Hash(text, rows));
    }

    public RenderedMessage Summary(SummaryView view)
    {
        var lang = view.Lang;

        if (view.FocusedLessonId is { } focusedId)
            return SummaryFocus(view, focusedId);

        var unmarked = view.Lines.Where(l => l.Status == LessonStatus.Scheduled).OrderBy(l => l.StartLocal).ToList();
        var marked = view.Lines.Where(l => l.Status != LessonStatus.Scheduled).OrderBy(l => l.StartLocal).ToList();

        return unmarked.Count == 0 ? SummaryClosed(view, marked) : SummaryList(view, unmarked, marked);
    }

    public string Toast(AppLanguage lang, NotificationToast toast, int count = 0) =>
        NotificationCopy.Toast(lang, toast, count);

    private RenderedMessage SummaryList(SummaryView view, List<DayLineView> unmarked, List<DayLineView> marked)
    {
        var lang = view.Lang;
        var lines = new List<string>
        {
            NotificationCopy.SummaryListHeader(lang, unmarked.Count, NotificationCopy.LessonWord(lang, unmarked.Count)),
            NotificationFormatting.WeekdayAndDate(lang, view.LocalDate),
        };
        lines.AddRange(marked.Select(l => RenderMarkedLine(lang, l)));

        var rows = new List<NotificationButtonRow>();
        var toShow = view.Expanded ? unmarked : unmarked.Take(6).ToList();
        foreach (var line in toShow)
            rows.Add(Row(SummaryButtonLabel(line), callback: $"s:{line.LessonId:N}"));

        if (!view.Expanded && unmarked.Count > 6)
            rows.Add(Row(NotificationCopy.MoreLessonsButton(lang, unmarked.Count - 6), callback: $"m:{view.LocalDate:yyyy-MM-dd}"));

        var allDoneLabel = marked.Count > 0 ? NotificationCopy.MarkRestButton(lang) : NotificationCopy.AllDoneButton(lang);
        rows.Add(Row(allDoneLabel, callback: $"a:{view.LocalDate:yyyy-MM-dd}"));

        var text = string.Join('\n', lines);
        var snapshot = SummarySnapshot(view);
        return new RenderedMessage(text, rows, snapshot, Hash(text, rows));
    }

    private RenderedMessage SummaryClosed(SummaryView view, List<DayLineView> marked)
    {
        var lang = view.Lang;
        var done = marked.Where(l => l.Status == LessonStatus.Completed).ToList();
        var cancelledCount = marked.Count(l => l.Status == LessonStatus.Cancelled);
        var earned = done.Sum(l => l.Price);
        var paid = done.Where(l => l.IsPaid).Sum(l => l.Price);
        var debt = earned - paid;

        var lines = new List<string>();
        if (cancelledCount == 0 && debt == 0)
        {
            lines.Add(NotificationCopy.DayClosedAllMarkedHeader(lang, done.Count, NotificationCopy.LessonWord(lang, done.Count)));
            lines.Add(NotificationCopy.AllPaidLine(lang, NotificationFormatting.Money(earned)));
        }
        else
        {
            lines.Add(NotificationCopy.DayClosedHeader(lang, NotificationFormatting.Money(earned)));
            if (done.Count > 0)
                lines.Add(NotificationCopy.DoneAndPaidLine(lang, done.Count, NotificationFormatting.Money(paid)));
            if (cancelledCount > 0)
                lines.Add(NotificationCopy.NotHappenedLine(lang, cancelledCount));
            if (debt > 0)
                lines.Add(NotificationCopy.DebtForDayLine(lang, NotificationFormatting.Money(debt)));
        }

        var rows = new List<NotificationButtonRow>();
        var miniAppUrl = options.Value.MiniAppUrl;
        if (view.TomorrowLessonsCount > 0 && !string.IsNullOrEmpty(miniAppUrl))
        {
            var tomorrow = view.LocalDate.AddDays(1);
            rows.Add(Row(
                NotificationCopy.TomorrowButton(lang, view.TomorrowLessonsCount, NotificationCopy.LessonWord(lang, view.TomorrowLessonsCount)),
                url: $"{miniAppUrl}?startapp=schedule-{tomorrow:yyyy-MM-dd}"));
        }

        var text = string.Join('\n', lines);
        var snapshot = SummarySnapshot(view);
        return new RenderedMessage(text, rows, snapshot, Hash(text, rows));
    }

    private RenderedMessage SummaryFocus(SummaryView view, Guid focusedId)
    {
        var lang = view.Lang;
        var focused = view.Lines.Single(l => l.LessonId == focusedId);
        var time = NotificationFormatting.Time(focused.StartLocal);
        var studentName = NotificationFormatting.Escape(focused.StudentName);

        var lines = new List<string> { NotificationCopy.SummaryFocusHeader(lang, time, studentName) };
        var priceText = focused.Price == 0 ? NotificationCopy.FreeLabel(lang) : NotificationFormatting.Money(focused.Price);
        var topic = string.IsNullOrWhiteSpace(focused.Topic) ? null : NotificationFormatting.Escape(focused.Topic);
        lines.Add(topic is null ? priceText : $"{topic} · {priceText}");

        var doneRow = new List<NotificationButton> { new(NotificationCopy.DoneButton(lang), CallbackData: $"c:{focused.LessonId:N}") };
        if (focused.Price > 0)
            doneRow.Add(new NotificationButton(NotificationCopy.DonePaidButton(lang), CallbackData: $"p:{focused.LessonId:N}"));

        var rows = new List<NotificationButtonRow>
        {
            new(doneRow),
            new(
            [
                new NotificationButton(NotificationCopy.NotHappenedButton(lang), CallbackData: $"x:{focused.LessonId:N}"),
                new NotificationButton(NotificationCopy.BackButton(lang), CallbackData: "b:"),
            ]),
        };

        var text = string.Join('\n', lines);
        var snapshot = SummarySnapshot(view);
        return new RenderedMessage(text, rows, snapshot, Hash(text, rows));
    }

    private static string RenderMarkedLine(AppLanguage lang, DayLineView line)
    {
        var time = NotificationFormatting.Time(line.StartLocal);
        var name = NotificationFormatting.Escape(line.StudentName);
        if (line.Status == LessonStatus.Cancelled)
            return $"<s>❌ {time} {name}</s>";

        var paidSuffix = line.IsPaid ? NotificationCopy.PaidSuffix(lang) : string.Empty;
        return $"✅ {time} {name}{paidSuffix}";
    }

    private static string SummaryButtonLabel(DayLineView line)
    {
        var time = NotificationFormatting.Time(line.StartLocal);
        var name = NotificationFormatting.Truncate(line.StudentName, 18);
        return NotificationFormatting.Truncate($"{time} · {name}", 25);
    }

    private static RenderedSnapshot SummarySnapshot(SummaryView view) => new(
        view.Lang.ToCode(),
        NotificationKind.DaySummary,
        null,
        new DaySnapshot(
            view.LocalDate,
            [.. view.Lines.Select(ToDayLineSnapshot)],
            [.. view.Lines.Select(l => l.LessonId)],
            view.Lines.ToDictionary(l => l.LessonId, l => l.StartLocal.ToUniversalTime())));

    private static DayLineSnapshot ToDayLineSnapshot(DayLineView line) =>
        new(line.LessonId, line.StartLocal.ToUniversalTime(), line.StudentName, line.Status, line.IsPaid, line.Price);

    /// <summary>
    /// "<i>оновлено 14:20 · +1 урок, 1 скасовано, 1 перенесено</i>" — the non-zero parts only, absent
    /// entirely when nothing changed since <see cref="AgendaView.BaselineLessonIds"/> was captured.
    /// </summary>
    private static string? BuildAgendaDeltaLine(AppLanguage lang, AgendaView view)
    {
        if (view.UpdatedAtLocal is not { } updatedAt || view.BaselineLessonIds.Count == 0)
            return null;

        var baseline = view.BaselineLessonIds.ToHashSet();
        var added = view.Lines.Count(l => !baseline.Contains(l.LessonId));
        var cancelled = view.Lines.Count(l => l.Status == LessonStatus.Cancelled && baseline.Contains(l.LessonId));
        var moved = view.Lines.Count(l =>
            baseline.Contains(l.LessonId)
            && view.BaselineStarts.TryGetValue(l.LessonId, out var baselineStart)
            && baselineStart != l.StartLocal.ToUniversalTime());

        var parts = new List<string>();
        if (added > 0)
            parts.Add(NotificationCopy.DeltaAdded(lang, added));
        if (cancelled > 0)
            parts.Add(NotificationCopy.DeltaCancelled(lang, cancelled));
        if (moved > 0)
            parts.Add(NotificationCopy.DeltaMoved(lang, moved));

        return parts.Count == 0
            ? null
            : NotificationCopy.UpdatedLine(lang, NotificationFormatting.Time(updatedAt), string.Join(", ", parts));
    }

    private static string RenderDayLine(AppLanguage lang, DayLineView line, bool compact)
    {
        var time = NotificationFormatting.Time(line.StartLocal);

        if (line.Status == LessonStatus.Completed)
            return $"✅ {time} {NotificationFormatting.Escape(line.StudentName)}";
        if (line.Status == LessonStatus.Cancelled)
            return $"<s>❌ {time} {NotificationFormatting.Escape(line.StudentName)}</s>";

        var name = NotificationFormatting.Escape(compact ? NotificationFormatting.ShortName(line.StudentName) : line.StudentName);
        if (compact)
            return $"{time} {name}";

        var suffixes = new List<string>();
        if (!string.IsNullOrWhiteSpace(line.Topic))
            suffixes.Add(NotificationFormatting.Escape(line.Topic));
        if (line.StudentDebt > 0)
            suffixes.Add(NotificationCopy.DebtSuffix(lang, NotificationFormatting.Money(line.StudentDebt)));
        if (!string.IsNullOrWhiteSpace(line.SeriesRule))
            suffixes.Add($"🔁 {NotificationFormatting.Escape(line.SeriesRule)}");

        return suffixes.Count == 0 ? $"{time} {name}" : $"{time} {name} · {string.Join(" · ", suffixes)}";
    }

    private static LessonStatus ToLessonStatus(ReminderPhase phase) => phase switch
    {
        ReminderPhase.Cancelled => LessonStatus.Cancelled,
        ReminderPhase.Completed => LessonStatus.Completed,
        _ => LessonStatus.Scheduled,
    };

    private static NotificationButtonRow Row(string label, string? callback = null, string? url = null) =>
        new([new NotificationButton(label, CallbackData: callback, Url: url)]);

    /// <summary>
    /// <c>SHA256(text + "␞" + every button label + "␟" + every button's CallbackData/Url)</c>, lower-case
    /// hex. The only place a hash is computed — never fed input fields directly, so it cannot drift from
    /// the text it covers.
    /// </summary>
    private static string Hash(string text, IReadOnlyList<NotificationButtonRow> rows)
    {
        var buttons = rows.SelectMany(r => r.Buttons).ToList();
        var labels = string.Concat(buttons.Select(b => b.Text));
        var payloads = string.Concat(buttons.Select(b => b.CallbackData ?? b.Url ?? string.Empty));
        var input = $"{text}␞{labels}␟{payloads}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
