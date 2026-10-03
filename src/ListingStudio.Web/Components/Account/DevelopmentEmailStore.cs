namespace ListingStudio.Web.Components.Account;

public sealed class DevelopmentEmailStore
{
    private readonly Dictionary<string, string> passwordResetLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object syncRoot = new();

    public void SetPasswordResetLink(string email, string link)
    {
        lock (syncRoot)
        {
            passwordResetLinks[email] = link;
        }
    }

    public bool TryGetPasswordResetLink(string email, out string? link)
    {
        lock (syncRoot)
        {
            return passwordResetLinks.TryGetValue(email, out link);
        }
    }
}
