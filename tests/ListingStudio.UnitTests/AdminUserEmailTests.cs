using ListingStudio.Domain.Administration;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class AdminUserEmailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailuresBackOffKeepTheLastErrorAndStopAfterTheMaximumAttempts()
    {
        var email = AdminUserEmail.Create(Guid.NewGuid(), "admin", "user", " user@example.com ", " Hello ", " Body ", Now);
        Assert.Equal("user@example.com", email.RecipientEmail);
        Assert.Equal("Hello", email.Subject);
        Assert.True(email.IsPending);

        Assert.True(email.RecordFailure(Now, new string('x', 900)));
        Assert.Equal(Now.AddMinutes(1), email.NextAttemptAtUtc);
        Assert.Equal(AdminUserEmail.MaximumErrorLength, email.LastError!.Length);
        Assert.True(email.RecordFailure(Now, "again"));
        Assert.Equal(Now.AddMinutes(2), email.NextAttemptAtUtc);

        for (var attempt = 3; attempt < AdminUserEmail.MaximumAttempts; attempt++)
        {
            Assert.True(email.RecordFailure(Now, "still down"));
        }

        Assert.False(email.RecordFailure(Now, "gave up"));
        Assert.False(email.IsPending);
        Assert.Equal(Now.AddMinutes(60), email.NextAttemptAtUtc);
    }

    [Fact]
    public void SendingClearsTheRetryState()
    {
        var email = AdminUserEmail.Create(Guid.NewGuid(), "admin", "user", "user@example.com", "Hello", "Body", Now);
        email.RecordFailure(Now, "SMTP down");
        email.MarkSent(Now.AddMinutes(1));

        Assert.False(email.IsPending);
        Assert.Equal(2, email.AttemptCount);
        Assert.Null(email.NextAttemptAtUtc);
        Assert.Null(email.LastError);
    }

    [Fact]
    public void SendingASensitiveEmailRedactsItsProtectedBody()
    {
        var email = AdminUserEmail.Create(
            Guid.NewGuid(), "admin", "user", "user@example.com", "Welcome", "encrypted-payload", Now,
            bodyIsProtected: true);

        email.MarkSent(Now.AddMinutes(1));

        Assert.False(email.BodyIsProtected);
        Assert.Equal("Sensitive invitation details were removed after delivery.", email.Body);
    }

    [Fact]
    public void RejectsMissingOrOversizedFields()
    {
        Assert.Throws<ArgumentException>(() => AdminUserEmail.Create(Guid.Empty, "admin", "user", "a@b.c", "s", "b", Now));
        Assert.Throws<ArgumentException>(() => AdminUserEmail.Create(Guid.NewGuid(), "admin", "user", "a@b.c", " ", "b", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdminUserEmail.Create(
            Guid.NewGuid(), "admin", "user", "a@b.c", new string('s', AdminUserEmail.MaximumSubjectLength + 1), "b", Now));
    }
}
