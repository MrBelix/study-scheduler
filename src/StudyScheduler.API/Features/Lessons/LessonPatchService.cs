using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Outcome of <see cref="LessonPatchService.ApplyAsync"/> — a closed set the endpoint maps onto its
/// HTTP results. Free of ASP.NET types so the pipeline stays testable without the HTTP stack.
/// </summary>
public abstract record LessonPatchOutcome
{
    private LessonPatchOutcome() { }

    /// <summary>The patch was applied and committed.</summary>
    public sealed record Ok(Lesson Lesson) : LessonPatchOutcome;

    /// <summary>One or more fields failed domain validation; nothing was saved.</summary>
    public sealed record Validation(Result Failure) : LessonPatchOutcome;

    /// <summary>The new time collides with existing lessons or series; nothing was saved.</summary>
    public sealed record Conflict(IReadOnlyList<LessonConflict> Conflicts) : LessonPatchOutcome;

    /// <summary>
    /// A concurrent request materialized the same series slot first — the caller should retry;
    /// the retry hits the physical row and applies as a plain update.
    /// </summary>
    public sealed record ConcurrentMaterialization : LessonPatchOutcome;
}

/// <summary>
/// Shared patch pipeline for physical lessons and freshly materialized slots: validate via the
/// domain mutators, check overlaps when the time actually changes (or the lesson is un-cancelled),
/// stage, commit once.
/// </summary>
public sealed class LessonPatchService(
    ILessonRepository lessons,
    LessonOverlapChecker overlapChecker,
    IUnitOfWork uow,
    ILogger<LessonPatchService> logger)
{
    /// <summary>
    /// Applies <paramref name="request"/> to <paramref name="lesson"/> and commits.
    /// <paramref name="excludeOccurrence"/> keeps a just-materialized slot from conflicting with its
    /// own series occurrence (the row is not persisted yet, so the checker would otherwise still see
    /// the slot as unmaterialized).
    /// </summary>
    public async Task<LessonPatchOutcome> ApplyAsync(
        Lesson lesson,
        UpdateLessonRequest request,
        long tutorId,
        bool isNew,
        SeriesSlot? excludeOccurrence = null,
        CancellationToken ct = default)
    {
        var startUtc = request.StartUtc ?? lesson.StartUtc;
        var durationMinutes = request.DurationMinutes ?? lesson.DurationMinutes;
        var status = request.Status ?? lesson.Status;

        // Overlap-check inputs are captured before the mutators run — un-cancelling in particular
        // compares against the lesson's pre-patch status.
        var timeChanged = startUtc != lesson.StartUtc || durationMinutes != lesson.DurationMinutes;
        var unCancelling = lesson.Status == LessonStatus.Cancelled && status != LessonStatus.Cancelled;

        // Domain mutators validate each field; failures are collected so one 400 still reports
        // every offending field, returned before the overlap check and before any save.
        var errors = new List<Error>();
        if (startUtc != lesson.StartUtc)
            lesson.Reschedule(startUtc);
        if (request.DurationMinutes is { } newDuration && newDuration != lesson.DurationMinutes)
            errors.AddRange(lesson.ChangeDuration(newDuration).Errors);
        if (request.Status is { } newStatus)
            errors.AddRange(lesson.ChangeStatus(newStatus).Errors);
        if (request.Price is { } price)
            errors.AddRange(lesson.SetPrice(price).Errors);
        if (request.IsPaid is { } isPaid)
            lesson.SetPaid(isPaid);
        if (request.Topic is not null)
            errors.AddRange(lesson.UpdateTopic(request.Topic).Errors);
        if (request.Description is not null)
            errors.AddRange(lesson.UpdateDescription(request.Description).Errors);
        if (errors.Count > 0)
            return new LessonPatchOutcome.Validation(Result.Failure([.. errors]));

        if (status != LessonStatus.Cancelled && (timeChanged || unCancelling))
        {
            var conflicts = await overlapChecker.CheckLessonAsync(
                tutorId, startUtc, startUtc.AddMinutes(durationMinutes),
                excludeLessonId: lesson.Id, excludeOccurrence: excludeOccurrence, ct: ct);
            if (conflicts.Count > 0)
                return new LessonPatchOutcome.Conflict(conflicts);
        }

        if (isNew)
            lessons.Add(lesson);
        else
            lessons.Update(lesson);

        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (isNew && SqlErrors.IsDuplicateKey(exception))
        {
            // A concurrent request materialized the same slot first — discard the doomed insert so
            // later saves in this scope don't retry it, and tell the caller to retry.
            uow.DiscardChanges();
            logger.LogWarning(
                exception,
                "Concurrent materialization of occurrence {OccurrenceDate} in series {SeriesId} detected; returning 409",
                lesson.OccurrenceDate, lesson.SeriesId);
            return new LessonPatchOutcome.ConcurrentMaterialization();
        }

        return new LessonPatchOutcome.Ok(lesson);
    }
}
