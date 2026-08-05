using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>
/// The one database session, and the single place tenancy is enforced: every tutor-owned table is
/// filtered by the scope's tutor on read and stamped with it on insert. A query that must span
/// tenants has to say so out loud with <c>IgnoreQueryFilters</c> — see
/// <see cref="ILessonSeriesRepository.GetStartedNotEndedAcrossAllTutorsAsync"/>,
/// <see cref="ITutorProfileRepository.GetNotifiableAcrossAllTutorsAsync"/> and
/// <see cref="INotificationDispatchRepository.GetTutorsWithLiveDispatchesAcrossAllTutorsAsync"/>,
/// the only three.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITutorContext tutor)
    : DbContext(options)
{
    /// <summary>
    /// The tenant of a scope that has none. Telegram ids are positive, so it matches no row: a
    /// tenant-less scope reads nothing instead of everything. Enforced in the database too — every
    /// tutor-owned table carries a <c>&gt; 0</c> check constraint, so no row can ever wear it.
    /// </summary>
    private const long NoTutor = 0;

    public DbSet<Student> Students => Set<Student>();

    public DbSet<TutorProfile> TutorProfiles => Set<TutorProfile>();

    public DbSet<LessonSeries> LessonSeries => Set<LessonSeries>();

    public DbSet<Lesson> Lessons => Set<Lesson>();

    public DbSet<NotificationDispatch> NotificationDispatches => Set<NotificationDispatch>();

    /// <summary>
    /// The tutor the global query filters compare against. An INSTANCE member on purpose: the model
    /// is built once and cached, but EF re-reads this property from the context that executes the
    /// query, so each scope filters by its own tenant — and a tenant established mid-scope (the
    /// background passes walk tenants one by one) applies to every query after it.
    /// </summary>
    private long CurrentTutorTelegramId => tutor.CurrentTutorTelegramId ?? NoTutor;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Applies to DateTimeOffset and DateTimeOffset? alike — see UtcTimestampConversion.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConversion>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Every tutor-owned table, enumerated against the configurations in this folder. Four carry
        // the owner as a column (StudentConfiguration, LessonConfiguration, LessonSeriesConfiguration,
        // NotificationDispatchConfiguration); TutorProfile is KEYED by the tutor id itself
        // (TutorProfileConfiguration), so its own key is its tenancy key.
        modelBuilder.Entity<Student>().HasQueryFilter(s => s.TutorTelegramId == CurrentTutorTelegramId);
        modelBuilder.Entity<Lesson>().HasQueryFilter(l => l.TutorTelegramId == CurrentTutorTelegramId);
        modelBuilder.Entity<LessonSeries>().HasQueryFilter(s => s.TutorTelegramId == CurrentTutorTelegramId);
        modelBuilder.Entity<NotificationDispatch>().HasQueryFilter(d => d.TutorTelegramId == CurrentTutorTelegramId);
        modelBuilder.Entity<TutorProfile>().HasQueryFilter(p => p.TelegramUserId == CurrentTutorTelegramId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Gives every row being inserted the scope's tutor unless it already names one, so ownership
    /// does not have to be threaded through the factory that built it. A row that already names THIS
    /// tutor passes untouched; a row that names ANOTHER one is refused outright — an insert whose
    /// owner disagrees with the scope that is inserting it would be a row written across the very
    /// boundary the filters draw, and no path in the app produces one (the background passes make
    /// each owner the scope's tenant BEFORE building its rows, so what they stage arrives owner-less).
    /// </summary>
    private void StampTenant()
    {
        if (tutor.CurrentTutorTelegramId is not { } tutorTelegramId)
            return;

        foreach (var entry in ChangeTracker.Entries<ITutorOwned>())
        {
            if (entry.State != EntityState.Added)
                continue;

            var owner = entry.Entity.TutorTelegramId;
            if (owner == tutorTelegramId)
                continue;

            if (owner != NoTutor)
                throw new InvalidOperationException(
                    $"Cannot insert {entry.Entity.GetType().Name} owned by tutor {owner} " +
                    $"while the scope belongs to tutor {tutorTelegramId}.");

            // Through EF rather than the entity: ownership has no domain setter, and does not want one.
            entry.Property(nameof(ITutorOwned.TutorTelegramId)).CurrentValue = tutorTelegramId;
        }
    }
}
