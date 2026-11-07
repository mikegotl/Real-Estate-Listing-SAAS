using ListingStudio.Application.Administration;

namespace ListingStudio.Web.Components.Administration;

/// <summary>
/// Serves the user-flow map (every Overview, Media and Campaign path with test case IDs) to platform admins. The page
/// and its CSV are generated from tools/flow-map/cases.py and embedded in this assembly, so they ship with the code
/// they describe.
/// </summary>
public static class AdminFlowMapEndpoints
{
    public const string PagePath = "/admin/flow-map/page";
    public const string CsvPath = "/admin/flow-map/test-cases.csv";

    private const string PageResource = "ListingStudio.Web.AdminFlowMap.user-flow-map.html";
    private const string CsvResource = "ListingStudio.Web.AdminFlowMap.user-flow-test-cases.csv";

    public static IEndpointRouteBuilder MapAdminFlowMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PagePath, () => Resource(PageResource, "text/html; charset=utf-8", downloadName: null))
            .RequireAuthorization(PlatformAdministration.Policy);
        app.MapGet(CsvPath, () => Resource(CsvResource, "text/csv; charset=utf-8", "user-flow-test-cases.csv"))
            .RequireAuthorization(PlatformAdministration.Policy);
        return app;
    }

    private static IResult Resource(string name, string contentType, string? downloadName)
    {
        var stream = typeof(AdminFlowMapEndpoints).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The flow map resource {name} is missing from the build.");
        return Results.Stream(stream, contentType, downloadName);
    }
}
