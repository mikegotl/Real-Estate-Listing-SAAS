namespace ListingStudio.Application.Videos;

public sealed class VideoPlanValidationException(
    string message,
    IReadOnlyList<string> validationErrors) : Exception(message)
{
    public IReadOnlyList<string> ValidationErrors { get; } = validationErrors;
}
