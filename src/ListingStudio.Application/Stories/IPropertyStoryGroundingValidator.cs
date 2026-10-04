using ListingStudio.Domain.Stories;

namespace ListingStudio.Application.Stories;

public interface IPropertyStoryGroundingValidator
{
    PropertyStoryGroundingResult Validate(
        PropertyStoryGenerationRequest request,
        PropertyStoryContent content);
}
