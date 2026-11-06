using System.Net;
using System.Text;
using ListingStudio.Application.Providers;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ProviderRequestExceptionTests
{
    [Fact]
    public async Task KeepsTheProvidersReplyWithoutCredentials()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\n  \"error\": \"Invalid duration 15\",\n  \"authorization\": \"Bearer sk_live_abcdefghijklmnop1234\"\n}",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.Add("x-request-id", "req-123");

        var exception = await ProviderRequestException.FromResponseAsync(response, "Higgsfield", "submit the video", ["my-secret-value"]);

        Assert.Equal("req-123", exception.ProviderRequestId);
        Assert.Contains("Invalid duration 15", exception.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_abcdefghijklmnop1234", exception.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.ResponseText, StringComparison.Ordinal);
        Assert.StartsWith("Higgsfield returned HTTP 400 when trying to submit the video (request req-123).", exception.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovesConfiguredSecretsAndCapsLength()
    {
        var text = ProviderRequestException.CleanResponse("token my-secret-value " + new string('x', 5_000), ["my-secret-value"]);

        Assert.NotNull(text);
        Assert.DoesNotContain("my-secret-value", text, StringComparison.Ordinal);
        Assert.True(text.Length <= ProviderRequestException.MaximumResponseLength);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void OnlyRefusalsThatCannotSucceedLaterArePermanent(HttpStatusCode status, bool permanent) =>
        Assert.Equal(permanent, new ProviderRequestException("Higgsfield", "submit", status, null).IsPermanent);

    [Fact]
    public void FindsAWrappedProviderError()
    {
        var inner = new ProviderRequestException("HeyGen", "create the agent presenter", HttpStatusCode.Forbidden, "quota exceeded");

        Assert.Same(inner, ProviderRequestException.Find(new InvalidOperationException("outer", inner)));
        Assert.Null(ProviderRequestException.Find(new InvalidOperationException("plain")));
        Assert.Contains("The provider gave no reason", new ProviderRequestException("OpenAI", "plan", HttpStatusCode.BadGateway, null).Describe(), StringComparison.Ordinal);
    }
}
