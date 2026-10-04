namespace ListingStudio.Application.Videos;

public interface IVideoDirector
{
    string DirectorVersion { get; }

    Task<DirectedEditorialPlan> DirectAsync(
        VideoDirectionRequest request,
        CancellationToken cancellationToken = default);
}
