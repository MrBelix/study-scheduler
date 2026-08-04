using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.API.Features.Profile;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// Tenancy as the database enforces it, over the real stack: the global query filters keep one
/// tutor's rows out of another's reads whatever the query, an insert takes its owner from the scope
/// instead of from the caller, and only the two deliberately named cross-tenant reads span tutors.
/// Each test uses distinct tutor ids so the shared database stays isolated between tests.
/// </summary>
[Collection(nameof(AppCollection))]
public class TenancyTests(AppFixture app)
{
    [Fact]
    public async Task Students_of_a_fresh_tutor_are_empty_while_another_tutor_has_rows()
    {
        const long tutorA = 5101;
        const long tutorB = 5102;
        var alice = TelegramInitData.ForUser(tutorA, "Alice");
        var bob = TelegramInitData.ForUser(tutorB, "Bob");

        var create = await app.Api.PostAs(alice, "/students", new { name = "Kid", rate = 100m });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<StudentDto>())!;

        // The read path is filtered, not merely narrowed by the endpoint: a tutor with no data of
        // their own sees nothing, even though the table is not empty.
        var bList = await (await app.Api.GetAs(bob, "/students")).Content.ReadFromJsonAsync<List<StudentDto>>();
        Assert.Empty(bList!);

        // Same query, same row, two tenants: only the owner's context can see it.
        await using var asOwner = app.CreateDbContext(tutorA);
        Assert.NotNull(await asOwner.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == created.Id));

        await using var asStranger = app.CreateDbContext(tutorB);
        Assert.Null(await asStranger.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == created.Id));
        Assert.Empty(await asStranger.Students.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Read_without_a_tenant_returns_nothing()
    {
        const long tutor = 5103;
        var alice = TelegramInitData.ForUser(tutor, "Alice");

        var create = await app.Api.PostAs(alice, "/students", new { name = "Kid", rate = 100m });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // Tenancy fails closed: a scope nobody claimed reads no rows rather than every row.
        await using var db = app.CreateDbContext(new TutorContext());
        Assert.Empty(await db.Students.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Insert_without_an_explicit_owner_is_stamped_with_the_scope_tutor()
    {
        const long tutor = 5104;

        await using var db = app.CreateDbContext(tutor);

        // The row arrives owner-less — the factory no longer takes a tutor id at all. Nothing but the
        // scope says whom it belongs to.
        var student = Student.Create("Owner-less", 50m, DateTimeOffset.UtcNow).Value;
        Assert.Equal(0, student.TutorTelegramId);
        db.Students.Add(student);

        await db.SaveChangesAsync();

        Assert.Equal(tutor, student.TutorTelegramId);
        await using var reread = app.CreateDbContext(tutor);
        var stored = await reread.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == student.Id);
        Assert.NotNull(stored);
        Assert.Equal(tutor, stored!.TutorTelegramId);
    }

    [Fact]
    public async Task Insert_that_already_names_the_scope_tutor_is_left_alone()
    {
        const long tutor = 5109;

        await using var db = app.CreateDbContext(tutor);

        // The fill path is only for rows that name nobody: a row that already names THIS tutor is
        // written exactly as handed over.
        var student = OwnedBy(Student.Create("Pre-owned", 50m, DateTimeOffset.UtcNow).Value, tutor);
        db.Students.Add(student);

        await db.SaveChangesAsync();

        await using var reread = app.CreateDbContext(tutor);
        var stored = await reread.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == student.Id);
        Assert.Equal(tutor, stored!.TutorTelegramId);
    }

    [Fact]
    public async Task Insert_that_names_another_tutor_is_refused()
    {
        const long tutor = 5110;
        const long otherTutor = 5111;

        await using var db = app.CreateDbContext(tutor);

        // A row whose owner disagrees with the scope inserting it would cross the very boundary the
        // filters draw — invisible to its writer, owned by someone who never asked for it. Refused
        // outright rather than silently written.
        var student = OwnedBy(Student.Create("Someone else's", 50m, DateTimeOffset.UtcNow).Value, otherTutor);
        db.Students.Add(student);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        // And nothing reached the database: the other tutor gained no row.
        await using var asOther = app.CreateDbContext(otherTutor);
        Assert.Empty(await asOther.Students.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Background_pass_stamps_each_tenants_inserts_with_that_tenant()
    {
        const long tutorA = 5112;
        const long tutorB = 5113;

        // One tenant-less scope walking tenants, exactly like the nightly generator: every row it
        // stages belongs to whoever the tenant was when it was saved.
        var tenant = new TutorContext();
        await using var db = app.CreateDbContext(tenant);

        tenant.SetForBackground(tutorA);
        var aRow = Student.Create("A's row", 50m, DateTimeOffset.UtcNow).Value;
        db.Students.Add(aRow);
        await db.SaveChangesAsync();

        tenant.SetForBackground(tutorB);
        var bRow = Student.Create("B's row", 50m, DateTimeOffset.UtcNow).Value;
        db.Students.Add(bRow);
        await db.SaveChangesAsync();

        Assert.Equal(tutorA, aRow.TutorTelegramId);
        Assert.Equal(tutorB, bRow.TutorTelegramId);
        await using var asA = app.CreateDbContext(tutorA);
        Assert.Equal("A's row", Assert.Single(await asA.Students.AsNoTracking().ToListAsync()).Name);
    }

    [Fact]
    public async Task Row_owned_by_the_no_tenant_sentinel_is_rejected_by_the_database()
    {
        const long tutor = 5114;

        await using var db = app.CreateDbContext(tutor);

        // The last line of defence behind the stamping: 0 is what a tenant-less scope filters by, so
        // a row wearing it would be everybody's. Only raw SQL can even try — and the CHECK stops it.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Students" ("Id", "TutorTelegramId", "Name", "Rate", "Status", "CreatedAtUtc")
            VALUES (@p0, 0, 'Ghost', 0, 'Active', now())
            """,
            Guid.NewGuid()));

        Assert.Equal("CK_Students_TutorTelegramIdPositive", insert.ConstraintName);
    }

    [Fact]
    public async Task Tenant_established_mid_scope_applies_to_the_queries_after_it()
    {
        const long tutorA = 5105;
        const long tutorB = 5106;
        var alice = TelegramInitData.ForUser(tutorA, "Alice");
        var bob = TelegramInitData.ForUser(tutorB, "Bob");

        Assert.Equal(
            HttpStatusCode.Created,
            (await app.Api.PostAs(alice, "/students", new { name = "A's kid", rate = 100m })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await app.Api.PostAs(bob, "/students", new { name = "B's kid", rate = 100m })).StatusCode);

        // What the background passes rely on: one scope, one context, the tenant moving from tutor to
        // tutor — every query after a switch must see the NEW tenant's rows, never the model's first.
        var tenant = new TutorContext();
        await using var db = app.CreateDbContext(tenant);

        tenant.SetForBackground(tutorA);
        var asA = await db.Students.AsNoTracking().ToListAsync();
        Assert.Equal("A's kid", Assert.Single(asA).Name);

        tenant.SetForBackground(tutorB);
        var asB = await db.Students.AsNoTracking().ToListAsync();
        Assert.Equal("B's kid", Assert.Single(asB).Name);
    }

    [Fact]
    public async Task Background_reads_span_tutors_only_through_the_methods_that_say_so()
    {
        const long tutorA = 5107;
        const long tutorB = 5108;
        var alice = TelegramInitData.ForUser(tutorA, "Alice");
        var bob = TelegramInitData.ForUser(tutorB, "Bob");
        var seriesA = await CreateWeeklySeries(alice);
        var seriesB = await CreateWeeklySeries(bob);

        // A tenant-less scope, exactly like the nightly generator's and the notification poller's.
        var tenant = new TutorContext();
        await using var db = app.CreateDbContext(tenant);
        var series = new EfLessonSeriesRepository(db);
        var profiles = new EfTutorProfileRepository(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var candidates = await series.GetStartedNotEndedAcrossAllTutorsAsync(
            startedOnOrBefore: today.AddMonths(6), notEndedBefore: today.AddDays(-1));
        var notifiable = await profiles.GetNotifiableAcrossAllTutorsAsync();

        // The two loud-named reads see every tenant — that is what makes one nightly tick cover all
        // tutors and one poll tick reach every notifiable chat.
        Assert.Contains(candidates, s => s.Id == seriesA.Id);
        Assert.Contains(candidates, s => s.Id == seriesB.Id);
        Assert.Contains(notifiable, p => p.TelegramUserId == tutorA);
        Assert.Contains(notifiable, p => p.TelegramUserId == tutorB);

        // Everything else on the very same repositories stays filtered: without a tenant they read
        // nothing at all, and once one is established they read that tutor's rows only.
        Assert.Empty(await series.GetAllAsync());

        tenant.SetForBackground(tutorA);
        // One tutor's series, and B's is not among them — the scope is the whole of the question now.
        Assert.Equal(seriesA.Id, Assert.Single(await series.GetAllAsync()).Id);
        // A profile is keyed BY the tutor id, so the filter sits on the key itself: the scope reaches
        // exactly its own profile, and another tutor's stays out of reach.
        Assert.Equal(tutorA, (await profiles.GetAsync())!.TelegramUserId);
        Assert.Contains(
            await series.GetStartedNotEndedAcrossAllTutorsAsync(
                startedOnOrBefore: today.AddMonths(6), notEndedBefore: today.AddDays(-1)),
            s => s.Id == seriesB.Id);
    }

    /// <summary>A tutor with a zone, a student and one weekly series — the state a background pass acts on.</summary>
    private async Task<SeriesDto> CreateWeeklySeries(string tutor)
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await app.Api.PutAs(tutor, "/profile", new { timeZoneId = "Europe/Kyiv" })).StatusCode);

        var student = await app.Api.PostAs(tutor, "/students", new { name = "Student", rate = 300m });
        Assert.Equal(HttpStatusCode.Created, student.StatusCode);
        var studentId = (await student.Content.ReadFromJsonAsync<StudentDto>())!.Id;

        var monday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        while (monday.DayOfWeek != DayOfWeek.Monday)
            monday = monday.AddDays(1);

        var created = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = monday,
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
            repeat = new { weekdays = "Monday" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<CreateDto>())!.Series!;
    }

    /// <summary>
    /// Presets ownership the way only persistence can — the domain deliberately exposes no setter, so
    /// a test that needs a row already naming a tutor plays EF's part with reflection.
    /// </summary>
    private static T OwnedBy<T>(T entity, long tutorTelegramId)
        where T : ITutorOwned
    {
        typeof(T).GetProperty(nameof(ITutorOwned.TutorTelegramId))!.SetValue(entity, tutorTelegramId);
        return entity;
    }

    private sealed record StudentDto(Guid Id, string Name);

    private sealed record SeriesDto(Guid Id, Guid StudentId, DateOnly StartDate, DateOnly? EndDate);

    private sealed record CreateDto(SeriesDto? Series);
}
