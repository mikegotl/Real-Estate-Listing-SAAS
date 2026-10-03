using ListingStudio.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace ListingStudio.Web.Components.Account;

internal sealed class DevelopmentIdentityEmailSender(
    IHostEnvironment environment,
    DevelopmentEmailStore emailStore) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        Task.CompletedTask;

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        Task.CompletedTask;

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("A production identity email provider has not been configured.");
        }

        emailStore.SetPasswordResetLink(email, resetLink);
        return Task.CompletedTask;
    }
}
