using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Authentication;
using Xunit;

namespace StudyScheduler.Tests;

public class TelegramInitDataValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UserJson =
        """{"id":42,"first_name":"Alice","last_name":"Doe","username":"alice","is_premium":true}""";

    private static TelegramInitDataValidator CreateValidator(TimeSpan? maxAge = null)
    {
        var options = Options.Create(new TelegramAuthOptions
        {
            BotToken = TelegramInitDataFactory.BotToken,
            MaxAuthAge = maxAge ?? TimeSpan.FromHours(24),
        });
        return new TelegramInitDataValidator(options, new FixedClock(Now));
    }

    private static Dictionary<string, string> ValidFields() => new()
    {
        ["auth_date"] = Now.ToUnixTimeSeconds().ToString(),
        ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
        ["user"] = UserJson,
    };

    [Fact]
    public void Validate_ValidData_ReturnsNoneAndParsesUser()
    {
        var initData = TelegramInitDataFactory.Query(ValidFields());

        var error = CreateValidator().Validate(initData, out var user);

        Assert.Equal(TelegramAuthError.None, error);
        Assert.NotNull(user);
        Assert.Equal(42, user!.Id);
        Assert.Equal("alice", user.Username);
        Assert.True(user.IsPremium);
    }

    // Regression guard: modern init data carries a separate `signature` field that Telegram does
    // NOT fold into the HMAC hash. If the validator wrongly included it, this would fail.
    [Fact]
    public void Validate_WithSignatureFieldPresent_StillValidates()
    {
        var fields = ValidFields();
        fields["signature"] = "abc123_this_is_a_third_party_ed25519_signature";

        var initData = TelegramInitDataFactory.Query(fields);

        var error = CreateValidator().Validate(initData, out var user);

        Assert.Equal(TelegramAuthError.None, error);
        Assert.NotNull(user);
    }

    [Fact]
    public void Validate_TamperedHash_ReturnsInvalidSignature()
    {
        var initData = TelegramInitDataFactory.Query(ValidFields(), hashOverride: new string('0', 64));

        var error = CreateValidator().Validate(initData, out var user);

        Assert.Equal(TelegramAuthError.InvalidSignature, error);
        Assert.Null(user);
    }

    [Fact]
    public void Validate_MalformedHashHex_ReturnsInvalidSignature()
    {
        var initData = TelegramInitDataFactory.Query(ValidFields(), hashOverride: "not-hex");

        var error = CreateValidator().Validate(initData, out _);

        Assert.Equal(TelegramAuthError.InvalidSignature, error);
    }

    [Fact]
    public void Validate_ExpiredAuthDate_ReturnsExpired()
    {
        var fields = ValidFields();
        fields["auth_date"] = Now.AddHours(-25).ToUnixTimeSeconds().ToString();

        var initData = TelegramInitDataFactory.Query(fields);

        var error = CreateValidator().Validate(initData, out _);

        Assert.Equal(TelegramAuthError.Expired, error);
    }

    [Fact]
    public void Validate_EmptyString_ReturnsMissingData()
    {
        var error = CreateValidator().Validate("", out var user);

        Assert.Equal(TelegramAuthError.MissingData, error);
        Assert.Null(user);
    }

    [Fact]
    public void Validate_NoHash_ReturnsMissingData()
    {
        var initData = TelegramInitDataFactory.RawQuery(ValidFields());

        var error = CreateValidator().Validate(initData, out _);

        Assert.Equal(TelegramAuthError.MissingData, error);
    }

    [Fact]
    public void Validate_ValidSignatureButNoUser_ReturnsMissingData()
    {
        var fields = new Dictionary<string, string>
        {
            ["auth_date"] = Now.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
        };

        var initData = TelegramInitDataFactory.Query(fields);

        var error = CreateValidator().Validate(initData, out var user);

        Assert.Equal(TelegramAuthError.MissingData, error);
        Assert.Null(user);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
