using Gcs.Domain.Users;

namespace Gcs.UnitTests.Domain.Users;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static User NewUser() => User.Create(Username.Create("Pilot.One").Value, "hash", UserRole.Operator, Now);

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("")]
    public void Usernames_are_simple_identifiers(string name) =>
        Username.Create(name).Error!.Code.ShouldBe("user.username.format");

    [Fact]
    public void Usernames_are_stored_lower_case_so_one_person_has_one_account() =>
        Username.Create("  Pilot.One ").Value.Value.ShouldBe("pilot.one");

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("            ")]
    public void Passwords_need_twelve_characters(string? password) =>
        User.ValidatePassword(password).Error!.Code.ShouldBe("user.password.length");

    [Fact]
    public void The_fifth_wrong_password_locks_the_account_for_five_minutes()
    {
        var user = NewUser();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.CanSignIn(Now).IsSuccess.ShouldBeTrue();
        user.RecordFailedLogin(Now);

        user.CanSignIn(Now.AddMinutes(4)).Error!.Code.ShouldBe("auth.locked_out");
        user.CanSignIn(Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_successful_login_resets_the_failure_count()
    {
        var user = NewUser();
        user.RecordFailedLogin(Now);
        user.RecordFailedLogin(Now);

        user.RecordSuccessfulLogin(Now);

        user.FailedLogins.ShouldBe(0);
        user.LastLoginAt.ShouldBe(Now);
    }

    [Fact]
    public void A_deactivated_user_gets_the_same_answer_as_a_wrong_password()
    {
        var user = NewUser();
        user.Deactivate(Now);

        user.CanSignIn(Now).Error.ShouldBe(UserErrors.InvalidCredentials);
    }

    [Fact]
    public void A_refresh_token_is_usable_until_it_expires_or_is_revoked_and_remembers_its_successor()
    {
        var token = RefreshToken.Issue(UserId.New(), "hash", Now, TimeSpan.FromHours(12));
        var successor = RefreshTokenId.New();

        token.IsUsableAt(Now.AddHours(11)).ShouldBeTrue();
        token.IsUsableAt(Now.AddHours(12)).ShouldBeFalse();
        token.Revoke(Now, successor);
        token.Revoke(Now.AddMinutes(1)); // a second revoke changes nothing

        token.IsUsableAt(Now).ShouldBeFalse();
        token.RevokedAt.ShouldBe(Now);
        token.ReplacedBy.ShouldBe(successor);
    }
}
