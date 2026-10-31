using System.Net;

namespace ListingStudio.Infrastructure.Administration;

/// <summary>Creates the branded HTML alternative used only for tester welcome emails.</summary>
public static class TesterWelcomeEmailTemplate
{
    public const string ProductionSiteUrl = "https://creativelistingstudio.com";

    public static string LoginUrl(string? configuredBaseUrl)
    {
        var configured = configuredBaseUrl?.Trim();
        var baseUrl = Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : ProductionSiteUrl;
        return $"{baseUrl}/Account/Login";
    }

    public static string CreateHtml(string textBody, string loginUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textBody);
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A valid site login URL is required.", nameof(loginUrl));
        }

        var safeLoginUrl = WebUtility.HtmlEncode(uri.AbsoluteUri);
        var normalizedBody = textBody.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = normalizedBody.Split("\n\n----\n", 2, StringSplitOptions.None);
        var safeBody = ToHtml(sections[0]);
        var safeFooter = sections.Length == 2 ? ToHtml(sections[1]) : string.Empty;
        var footerRow = safeFooter.Length == 0 ? string.Empty : $"""
            <tr>
              <td style="border-top:1px solid #ece6dc;padding:20px 34px 24px;color:#7a858f;font-size:11px;line-height:1.55;text-align:center;">{safeFooter}</td>
            </tr>
            """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Welcome to Creative Listing Studio</title>
            </head>
            <body style="margin:0;padding:0;background:#f4f1eb;color:#17202a;font-family:Arial,Helvetica,sans-serif;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background:#f4f1eb;padding:28px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:620px;background:#ffffff;border:1px solid #e6dfd3;border-radius:18px;overflow:hidden;box-shadow:0 10px 28px rgba(15,31,48,.10);">
                      <tr>
                        <td style="background:#0d2033;padding:26px 32px;">
                          <table role="presentation" cellspacing="0" cellpadding="0" border="0">
                            <tr>
                              <td style="width:46px;height:46px;background:#d8a94a;border-radius:12px;text-align:center;color:#0d2033;font-size:27px;font-weight:bold;line-height:46px;">✦</td>
                              <td style="padding-left:14px;color:#ffffff;font-size:22px;font-weight:700;letter-spacing:.2px;">Creative Listing Studio</td>
                            </tr>
                          </table>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:34px 34px 12px;">
                          <div style="color:#a16f16;font-size:13px;font-weight:700;letter-spacing:1.4px;text-transform:uppercase;">Tester access</div>
                          <h1 style="margin:8px 0 18px;color:#0d2033;font-size:30px;line-height:1.2;">Your creative workspace is ready</h1>
                          <div style="font-size:16px;line-height:1.65;color:#344352;">{{safeBody}}</div>
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:22px 34px 14px;">
                          <a href="{{safeLoginUrl}}" style="display:inline-block;background:#d8a94a;color:#0d2033;text-decoration:none;font-size:17px;font-weight:700;padding:15px 28px;border-radius:10px;box-shadow:0 4px 10px rgba(161,111,22,.22);">Open CreativeListingStudio.com&nbsp; →</a>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:8px 34px 34px;text-align:center;color:#677481;font-size:12px;line-height:1.5;">
                          If the button does not open, copy this address:<br>
                          <a href="{{safeLoginUrl}}" style="color:#6d531e;word-break:break-all;">{{safeLoginUrl}}</a>
                        </td>
                      </tr>
                      {{footerRow}}
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string ToHtml(string value) => WebUtility.HtmlEncode(value)
        .Replace("\n", "<br>", StringComparison.Ordinal);
}
