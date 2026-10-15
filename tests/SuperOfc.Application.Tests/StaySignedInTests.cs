using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Application.Users;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>"Keep me signed in on this PC": tokens issued at sign-in, used at start-up, and ended by sign-out and password changes.</summary>
public class StaySignedInTests
{
    private static async Task<string> RememberAsync(TestHost host)
    {
        host.Session.SignOut();
        var user = await host.SendOkAsync(new LoginCommand("admin", TestHost.AdminPassword, Remember: true));
        host.Session.SignOut();
        return user.RememberToken!;
    }

    [Fact]
    public async Task A_kept_token_signs_in_without_the_password_and_only_its_hash_is_stored()
    {
        await using var host = await TestHost.CreateAsync();
        var token = await RememberAsync(host);

        var user = await host.SendOkAsync(new ResumeSignInCommand(token));

        user.Username.Should().Be("admin");
        host.Session.IsAuthenticated.Should().BeTrue();
        var stored = await host.WithDbAsync(db => db.SignInTokens.SingleAsync());
        stored.TokenHash.Should().NotContain(token).And.Be(SignInTokens.Hash(token));
        stored.ExpiresAtUtc.Should().Be(host.Clock.UtcNow + SignInToken.Lifetime);
    }

    [Fact]
    public async Task Without_keep_signed_in_no_token_is_issued()
    {
        await using var host = await TestHost.CreateAsync();
        host.Session.SignOut();

        var user = await host.SendOkAsync(new LoginCommand("admin", TestHost.AdminPassword));

        user.RememberToken.Should().BeNull();
        (await host.WithDbAsync(db => db.SignInTokens.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_token_lasts_thirty_days_from_its_last_use()
    {
        await using var host = await TestHost.CreateAsync();
        var token = await RememberAsync(host);

        host.Clock.Advance(TimeSpan.FromDays(25));
        await host.SendOkAsync(new ResumeSignInCommand(token));
        host.Session.SignOut();
        host.Clock.Advance(TimeSpan.FromDays(25));
        await host.SendOkAsync(new ResumeSignInCommand(token));
        host.Session.SignOut();

        host.Clock.Advance(TimeSpan.FromDays(31));
        (await host.SendFailAsync(new ResumeSignInCommand(token))).Code.Should().Be("Auth.SignInExpired");
        host.Session.IsAuthenticated.Should().BeFalse();
        (await host.WithDbAsync(db => db.SignInTokens.CountAsync())).Should().Be(0, "an expired token is removed");
    }

    [Fact]
    public async Task Signing_out_ends_the_token()
    {
        await using var host = await TestHost.CreateAsync();
        var token = await RememberAsync(host);
        await host.SendOkAsync(new ResumeSignInCommand(token));

        await host.SendOkAsync(new LogoutCommand(token));

        (await host.SendFailAsync(new ResumeSignInCommand(token))).Code.Should().Be("Auth.SignInExpired");
    }

    [Fact]
    public async Task Unknown_tokens_do_not_sign_in()
    {
        await using var host = await TestHost.CreateAsync();
        host.Session.SignOut();

        (await host.SendFailAsync(new ResumeSignInCommand("made-up"))).Code.Should().Be("Auth.SignInExpired");
        (await host.SendFailAsync(new ResumeSignInCommand(""))).Code.Should().Be("Auth.SignInExpired");
        host.Session.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task Password_reset_change_and_deactivation_end_every_token_of_the_user()
    {
        await using var host = await TestHost.CreateAsync();
        var clerk = await host.SendOkAsync(new CreateUserCommand("clerk", "Clerk", "Clerk12345", Role.HR));

        async Task<string> RememberClerkAsync()
        {
            host.Session.SignOut();
            var signedIn = await host.SendOkAsync(new LoginCommand("clerk", "Clerk12345", Remember: true));
            return signedIn.RememberToken!;
        }

        async Task AsAdminAsync()
        {
            host.Session.SignOut();
            await host.SendOkAsync(new LoginCommand("admin", TestHost.AdminPassword));
        }

        var token = await RememberClerkAsync();
        await host.SendOkAsync(new ChangeOwnPasswordCommand("Clerk12345", "Clerk12345"));
        host.Session.SignOut();
        (await host.SendFailAsync(new ResumeSignInCommand(token))).Code.Should().Be("Auth.SignInExpired", "changing the password ends it");

        token = await RememberClerkAsync();
        await AsAdminAsync();
        await host.SendOkAsync(new ResetPasswordCommand(clerk, "Clerk12345"));
        host.Session.SignOut();
        (await host.SendFailAsync(new ResumeSignInCommand(token))).Code.Should().Be("Auth.SignInExpired", "a reset ends it");

        token = await RememberClerkAsync();
        await AsAdminAsync();
        await host.SendOkAsync(new UpdateUserCommand(clerk, "Clerk", Role.HR, IsActive: false));
        host.Session.SignOut();
        (await host.SendFailAsync(new ResumeSignInCommand(token))).Code.Should().Be("Auth.SignInExpired", "a deactivated user cannot come back in");
    }
}
