using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Outcome of <see cref="LessonPatchService.ApplyAsync"/> — a closed set of cases the endpoint
/// maps onto its HTTP results. Deliberately free of ASP.NET types so the patch pipeline stays
/// testable without the HTTP stack.
/// </summary>
public abstract record LessonPatchOutcome
{
    // Private ctor closes the hierarchy: only the nested cases below can derive.
    private LessonPatchOutcome() { }

    /// <summary>The patch was applied and committed.</summary>
    public sealed record Ok(Lesson Lesson) : LessonPatchOutcome;

    /// <summary>One or more fields failed domain validation; nothing was saved.</summary>
    public sealed record Validation(Result Failure) : LessonPatchOutcome;

    /// <summary>The new time collides with existing lessons or series; nothing was saved.</summary>
    public sealed record Conflict(List<LessonConflict> Conflicts) : LessonPatchOutcome;

    /// <summary>
    /// A concurrent request materialized the same series slot first — the caller should retry;
    /// the retry will hit the physical row and apply as a plain update.
    /// </summary>
    public sealed record ConcurrentMaterialization : LessonPatchOutcome;
}

/// <summary>
/// Shared patch pipeline for physical lessons and freshly materialized slots: validate via the
/// domain mutators, check overlaps when the time actually changes (or the lesson is
/// un-cancelled), stage, commit once.
/// </summary>
public sealed class LessonPatchService(
    ILessonRepository lessons,
    LessonOverlapChecker overlapChecker,
    IUnitOfWork uow,
    ILogger<LessonPatchService> logger)
{
    /// <summary>
    /// Applies <paramref name="request"/> to <paramref name="lesson"/> and commits.
    /// <paramref name="excludeOccurrence"/> keeps a just-materialized slot from conflicting with
    /// its own series occurrence (the row is not persisted yet, so the checker would otherwise
    /// still see the slot as an unmaterialized occurrence).
    /// </summary>
    public async Task<LessonPatchOutcome> ApplyAsync(
        Lesson lesson,
        UpdateLessonRequest request,
        long tutorId,
        bool isNew,
        (Guid SeriesId, DateOnly OccurrenceDate)? excludeOccurrence = null,
        CancellationToken ct = default)
    {
        var startUtc = request.StartUtc ?? lesson.StartUtc;
        var durationMinutes = request.DurationMinutes ?? lesson.DurationMinutes;
        var status = request.Status ?? lesson.Status;

        // Overlap-check inputs are captured before the mutators run — un-cancelling in
        // particular compares against the lesson's pre-patch status.
        var timeChanged = startUtc != lesson.StartUtc || durationMinutes != lesson.DurationMinutes;
        var unCancelling = lesson.Status == LessonStatus.Cancelled && status != LessonStatus.Cancelled;

        // Domain mutators validate each field; failures are collected so one 400 still reports
        // every offending field, and it is returned before the overlap check and before any
        // save — invalid input must never answer 409 or persist a partial patch.
        var errors = new List<Error>();
        if (timeChanged)
            errors.AddRange(lesson.Reschedule(startUtc, durationMinutes).Errors);
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
            // A concurrent request materialized the same slot first — the unique
            // (SeriesId, OccurrenceDate) index rejected the insert (duplicate-key only; any
            // other constraint failure propagates to the global handler).
            logger.LogWarning(
                exception,
                "Concurrent materialization of occurrence {OccurrenceDate} in series {SeriesId} detected; returning 409",
                lesson.OccurrenceDate, lesson.SeriesId);

            return new LessonPatchOutcome.ConcurrentMaterialization();
        }

        return new LessonPatchOutcome.Ok(lesson);
    }
}
