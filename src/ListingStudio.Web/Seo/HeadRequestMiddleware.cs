namespace ListingStudio.Web.Seo;

/// <summary>
/// Answers HEAD for pages as if it were GET without a body. Blazor page endpoints only accept GET and POST,
/// so link checkers and preview bots that send HEAD first otherwise see 405 and report the page as broken.
/// </summary>
public sealed class HeadRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        context.Request.Method = HttpMethods.Get;
        var body = context.Response.Body;
        context.Response.Body = Stream.Null;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = body;
        }
    }
}
