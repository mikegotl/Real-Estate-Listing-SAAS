using ListingStudio.Infrastructure.Identity;

namespace ListingStudio.Web.Authentication;

public sealed class RequiredPasswordChangeMiddleware(RequestDelegate next)
{
    private static readonly PathString ChangePasswordPath = new("/Account/ChangeTemporaryPassword");

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(
                ListingStudioUserClaimsPrincipalFactory.RequiresPasswordChangeClaim,
                bool.TrueString)
            && !IsAllowed(context.Request.Path))
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Redirect(ChangePasswordPath);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }

            return;
        }

        await next(context);
    }

    private static bool IsAllowed(PathString path) =>
        path.StartsWithSegments(ChangePasswordPath)
        || path.StartsWithSegments("/Account/Logout")
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/favicon")
        || IsStaticAsset(path);

    private static bool IsStaticAsset(PathString path)
    {
        var extension = Path.GetExtension(path.Value);
        return extension is ".css" or ".js" or ".map" or ".woff" or ".woff2" or ".png" or ".jpg" or ".jpeg" or ".svg" or ".webp" or ".ico";
    }
}
