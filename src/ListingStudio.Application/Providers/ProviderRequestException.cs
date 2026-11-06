using System.Net;
using System.Text.RegularExpressions;

namespace ListingStudio.Application.Providers;

/// <summary>
/// An external provider answered with an HTTP error. Keeps what the provider said (secrets removed) and, where the
/// caller sets it, the request that was sent, so the failure can be shown to the agent and to administrators.
/// The reply is never part of <see cref="Exception.Message"/>, so logs keep only the status and request ID.
/// </summary>
public sealed partial class ProviderRequestException : HttpRequestException
{
    public const int MaximumResponseLength = 2_000;
    public const int MaximumShortResponseLength = 400;

    public ProviderRequestException(
        string provider,
        string action,
        HttpStatusCode statusCode,
        string? responseText,
        string? providerRequestId = null)
        : base(
            string.IsNullOrWhiteSpace(providerRequestId)
                ? $"{provider} returned HTTP {(int)statusCode} when trying to {action}."
                : $"{provider} returned HTTP {(int)statusCode} when trying to {action} (request {providerRequestId.Trim()}).",
            null,
            statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Provider = provider.Trim();
        Action = action.Trim();
        ResponseText = string.IsNullOrWhiteSpace(responseText) ? null : responseText;
        ProviderRequestId = string.IsNullOrWhiteSpace(providerRequestId) ? null : providerRequestId.Trim();
    }

    public string Provider { get; }

    /// <summary>What was being done, in plain words, such as "submit the Kling 4 video".</summary>
    public string Action { get; }

    /// <summary>The provider's own reply, whitespace collapsed, secrets removed, at most 2,000 characters.</summary>
    public string? ResponseText { get; }

    public string? ProviderRequestId { get; }

    /// <summary>The request body that was sent (no credentials: they travel in headers), when the caller knows it.</summary>
    public string? RequestBody { get; set; }

    /// <summary>One line for the agent: who refused, the status and what the provider said.</summary>
    public string Describe(int maximumLength = 1_000)
    {
        var text = ResponseText is null
            ? $"{Message} The provider gave no reason."
            : $"{Message} {Provider} said: {Shorten(ResponseText, MaximumShortResponseLength)}";
        return Shorten(text, maximumLength);
    }

    /// <summary>
    /// Reads an error response into an exception. The body is read only once and capped, and every secret passed in
    /// (API keys, tokens) and anything that looks like a bearer or key header is removed.
    /// </summary>
    public static async Task<ProviderRequestException> FromResponseAsync(
        HttpResponseMessage response,
        string provider,
        string action,
        IEnumerable<string?>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            // An unreadable body still leaves the status code.
        }

        var requestId = FirstHeader(response, "x-request-id") ?? FirstHeader(response, "request-id");
        return new ProviderRequestException(
            provider,
            action,
            response.StatusCode,
            CleanResponse(body, secrets),
            requestId);
    }

    /// <summary>The provider error inside an exception, however deeply it is wrapped.</summary>
    public static ProviderRequestException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderRequestException provider)
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// A request the provider refused as invalid or unauthorized fails the same way every time it is sent again.
    /// Timeouts, rate limits, busy answers and server errors can succeed later.
    /// </summary>
    public bool IsPermanent => StatusCode is { } status
        && (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.RequestTimeout
            or HttpStatusCode.Conflict
            or (HttpStatusCode)425
            or HttpStatusCode.TooManyRequests);

    public static string? CleanResponse(string? body, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var text = body.Length > 20_000 ? body[..20_000] : body;
        foreach (var secret in secrets ?? [])
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
            {
                text = text.Replace(secret, "[removed]", StringComparison.Ordinal);
            }
        }

        text = CredentialPattern().Replace(text, "$1[removed]");
        text = WhitespacePattern().Replace(text, " ").Trim();
        text = new string(text.Where(character => !char.IsControl(character)).ToArray());
        return text.Length == 0 ? null : Shorten(text, MaximumResponseLength);
    }

    private static string? FirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string Shorten(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"(?i)((?:bearer|key|api[_-]?key|token|secret|authorization)[""']?\s*[:=]?\s*[""']?)[A-Za-z0-9_\-\.:]{12,}")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
