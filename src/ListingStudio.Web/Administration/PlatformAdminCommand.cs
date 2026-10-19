using ListingStudio.Infrastructure.Administration;

namespace ListingStudio.Web.Administration;

public static class PlatformAdminCommand
{
    public static async Task<bool> TryExecuteAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var grantIndex = Array.IndexOf(args, "--grant-platform-admin");
        var revokeIndex = Array.IndexOf(args, "--revoke-platform-admin");
        if (grantIndex < 0 && revokeIndex < 0)
        {
            return false;
        }

        if ((grantIndex >= 0 && revokeIndex >= 0)
            || (grantIndex >= 0 && grantIndex + 1 >= args.Length)
            || (revokeIndex >= 0 && revokeIndex + 1 >= args.Length))
        {
            throw new ArgumentException(
                "Supply exactly one of --grant-platform-admin <existing-email> or --revoke-platform-admin <existing-email>.");
        }

        await using var scope = services.CreateAsyncScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>();
        var changed = grantIndex >= 0
            ? await bootstrapper.GrantAsync(args[grantIndex + 1], cancellationToken)
            : await bootstrapper.RevokeAsync(args[revokeIndex + 1], cancellationToken);
        Console.WriteLine(changed
            ? "Platform administrator access changed. Existing sessions must sign in again."
            : "Platform administrator access was already in the requested state.");
        return true;
    }
}
