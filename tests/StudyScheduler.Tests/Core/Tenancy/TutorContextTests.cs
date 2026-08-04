using StudyScheduler.API.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Core.Tenancy;

/// <summary>
/// The scope's tutor: who may set it, and who may not. Everything the database filters and stamps
/// hangs off this one value.
/// </summary>
public class TutorContextTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 777;

    [Fact]
    public void CurrentTutorTelegramId_BeforeAnythingIsEstablished_IsNull()
    {
        // Arrange
        var sut = new TutorContext();

        // Act
        var current = sut.CurrentTutorTelegramId;

        // Assert
        // A scope nobody claimed belongs to nobody — the query filters then match no row at all.
        Assert.Null(current);
    }

    [Fact]
    public void SetFromAuthentication_ValidatedTelegramId_BecomesTheCurrentTutor()
    {
        // Arrange
        var sut = new TutorContext();

        // Act
        sut.SetFromAuthentication(Tutor);

        // Assert
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetFromAuthentication_CalledAgainWithTheSameTutor_IsANoOp()
    {
        // Arrange
        // The tenancy middleware can legitimately run twice over one scope (a re-executed pipeline),
        // and re-establishing the very same identity changes nothing.
        var sut = new TutorContext();
        sut.SetFromAuthentication(Tutor);

        // Act
        sut.SetFromAuthentication(Tutor);

        // Assert
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetFromAuthentication_CalledAgainWithADifferentTutor_ThrowsAndKeepsTheFirst()
    {
        // Arrange
        // Work has already been done as the first tutor; moving the scope now would re-point every
        // query filter and every insert stamp under it.
        var sut = new TutorContext();
        sut.SetFromAuthentication(Tutor);

        // Act
        var act = () => sut.SetFromAuthentication(OtherTutor);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetFromAuthentication_AfterADifferentBackgroundTenant_ThrowsAndKeepsIt()
    {
        // Arrange
        // The reverse order of the guard below: a scope already working as one tutor cannot be
        // reassigned by an authentication either.
        var sut = new TutorContext();
        sut.SetForBackground(Tutor);

        // Act
        var act = () => sut.SetFromAuthentication(OtherTutor);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetForBackground_TenantLessScope_BecomesTheCurrentTutor()
    {
        // Arrange
        var sut = new TutorContext();

        // Act
        sut.SetForBackground(Tutor);

        // Assert
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetForBackground_CalledRepeatedly_MovesToEachTutorInTurn()
    {
        // Arrange
        // The nightly generator and the notification poller walk tenants one at a time in one scope.
        var sut = new TutorContext();

        // Act
        sut.SetForBackground(Tutor);
        sut.SetForBackground(OtherTutor);

        // Assert
        Assert.Equal(OtherTutor, sut.CurrentTutorTelegramId);
    }

    [Fact]
    public void SetForBackground_AfterAuthentication_ThrowsAndKeepsTheAuthenticatedTutor()
    {
        // Arrange
        // The scope belongs to an authenticated request, so its tutor is settled by the init data.
        var sut = new TutorContext();
        sut.SetFromAuthentication(Tutor);

        // Act
        var act = () => sut.SetForBackground(OtherTutor);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(Tutor, sut.CurrentTutorTelegramId);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void SetFromAuthentication_NonPositiveTelegramId_Throws(long tutorTelegramId)
    {
        // Arrange
        var sut = new TutorContext();

        // Act
        var act = () => sut.SetFromAuthentication(tutorTelegramId);

        // Assert
        // 0 is the "no tenant" sentinel the query filters use; it must never come from a caller.
        Assert.Throws<ArgumentOutOfRangeException>(act);
    }
}
