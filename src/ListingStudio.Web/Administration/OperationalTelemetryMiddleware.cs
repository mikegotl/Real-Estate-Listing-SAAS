using ListingStudio.Application.Administration;

namespace ListingStudio.Web.Administration;

public sealed class OperationalTelemetryMiddleware(
    RequestDelegate next,
    IOperationalEventRecorder recorder,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var shouldRecord = ShouldRecord(context.Request.Path);
        try
        {
            await next(context);
        }
        catch
        {
            if (shouldRecord)
            {
                await RecordAsync(context.Request.Path, "5xx");
            }

            throw;
        }

        if (shouldRecord)
        {
            await RecordAsync(context.Request.Path, $"{context.Response.StatusCode / 100}xx");
        }
    }

    private ValueTask RecordAsync(PathString path, string outcome) =>
        recorder.RecordAsync(new OperationalObservation(
            "request",
            RouteCategory(path),
            outcome,
            timeProvider.GetUtcNow()));

    private static bool ShouldRecord(PathString path) =>
        !path.StartsWithSegments("/_blazor")
        && !path.StartsWithSegments("/health")
        && !path.StartsWithSegments("/css")
        && !path.StartsWithSegments("/js")
        && !path.StartsWithSegments("/lib")
        && path.Value?.Contains('.', StringComparison.Ordinal) != true;

    private static string RouteCategory(PathString path)
    {
        if (path.StartsWithSegments("/admin") || path.StartsWithSegments("/api/admin")) return "admin";
        if (path.StartsWithSegments("/Account")) return "account";
        if (path.StartsWithSegments("/properties")) return "properties";
        if (path.StartsWithSegments("/property-media")) return "property-media";
        if (path.StartsWithSegments("/campaigns")) return "campaigns";
        if (path.StartsWithSegments("/billing")) return "billing";
        if (path == "/") return "home";
        return "other";
    }
}
