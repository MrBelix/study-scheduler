using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Features.Notifications;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Builds the Lessons feature's one façade over in-memory fakes, with its real collaborators
/// (generator, overlap checker) — the wiring the endpoint tests would otherwise repeat. Only the
/// seams a test varies (unit of work, clock) are parameters.
/// </summary>
internal static class LessonServiceFactory
{
    /// <param name="tenant">
    /// The scope the service runs in — the same instance the fakes filter by, so the façade and its
    /// repositories agree on whose data this is, exactly as the request scope makes them agree in
    /// production. Generic over the scope type so a recording one fits too.
    /// </param>
    /// <param name="profiles">
    /// Read by the create path only, so the series tests leave it at the empty default.
    /// </param>
    public static LessonService Create<TScope>(
        TScope tenant,
        ILessonRepository lessons,
        ILessonSeriesRepository series,
        IStudentRepository students,
        IUnitOfWork uow,
        TimeProvider clock,
        ITutorProfileRepository? profiles = null)
        where TScope : ITutorContext, ITutorScope
    {
        var overlapChecker = new LessonOverlapChecker(
            lessons, series, tenant, NullLogger<LessonOverlapChecker>.Instance);

        return new LessonService(
            lessons,
            series,
            students,
            profiles ?? new FakeTutorProfileRepository(tenant),
            new LessonGenerator(
                lessons, series, students, uow, tenant, clock,
                NullLogger<LessonGenerator>.Instance),
            overlapChecker,
            uow,
            clock,
            NullLogger<LessonService>.Instance);
    }
}
