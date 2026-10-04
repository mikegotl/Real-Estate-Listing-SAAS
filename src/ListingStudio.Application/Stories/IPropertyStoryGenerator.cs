using ListingStudio.Domain.Stories;

namespace ListingStudio.Application.Stories;

public interface IPropertyStoryGenerator
{
    string GenerationVersion { get; }

    Task<PropertyStoryContent> GenerateAsync(
        PropertyStoryGenerationRequest request,
        CancellationToken cancellationToken = default);
}
